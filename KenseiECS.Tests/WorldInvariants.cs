using System.Collections.Generic;
using NUnit.Framework;

namespace KenseiECS.Tests {
    // Cross-checks a world's bookkeeping against its pools: masks, component counts,
    // dead slots, pool dense/sparse agreement, filter membership and owning groups.
    internal static class WorldInvariants {
        public static void AssertConsistent(World world, string when) {
            var errors = Check(world);
            Assert.That(errors, Is.Empty, $"world bookkeeping {when}:\n  " + string.Join("\n  ", errors));
        }

        public static List<string> Check(World world) {
            var errors = new List<string>();
            var pools = world._pools;
            var masks = world._componentMasks;
            var counts = world._componentCounts;
            var alive = world._alive;
            int next = world._nextIndex;

            foreach (var pool in pools) {
                if (pool == null) {
                    continue;
                }
                string name = pool.ComponentType.Name;
                var dense = pool.RawEntities;
                for (int i = 0; i < pool.Count; i++) {
                    int e = dense[i];
                    if (!pool.Has(e) || pool.GetDenseIndex(e) != i) {
                        errors.Add($"{name}: dense[{i}] = {e} is not mapped back to {i}");
                    }
                    if (e >= next || !alive[e]) {
                        errors.Add($"{name}: dead slot {e} still holds a component");
                    }
                }
            }

            int aliveCount = 0;
            for (int e = 0; e < next; e++) {
                if (alive[e]) {
                    aliveCount++;
                }
                int held = 0;
                foreach (var pool in pools) {
                    if (pool == null) {
                        continue;
                    }
                    bool has = pool.Has(e);
                    int t = pool.TypeIndex;
                    bool bit = (t >> 6) < masks.Length && ((masks[t >> 6][e] >> (t & 63)) & 1) != 0;
                    if (has) {
                        held++;
                    }
                    if (has != bit) {
                        errors.Add($"slot {e}: Pool<{pool.ComponentType.Name}>.Has = {has} but mask bit = {bit}");
                    }
                }
                if (alive[e] && counts[e] != held) {
                    errors.Add($"slot {e}: component count {counts[e]}, pools hold {held}");
                }
                if (alive[e] && held == 0) {
                    errors.Add($"slot {e}: alive without components");
                }
            }
            if (world.EntityCount != aliveCount) {
                errors.Add($"EntityCount {world.EntityCount}, alive slots {aliveCount}");
            }

            for (int f = 0; f < world.FilterCount; f++) {
                var filter = world.GetFilter(f);
                int matches = 0;
                for (int e = 0; e < next; e++) {
                    bool expected = alive[e] && Matches(world, filter, e);
                    if (expected) {
                        matches++;
                    }
                    if (filter.Contains(e) != expected) {
                        errors.Add($"{filter}: Contains({e}) = {filter.Contains(e)}, expected {expected}");
                    }
                }
                if (filter.Count != matches) {
                    errors.Add($"{filter}: Count {filter.Count}, matching entities {matches}");
                }
            }

            for (int g = 0; g < world.GroupCount; g++) {
                var group = world.GetGroup(g);
                int expected = 0;
                for (int e = 0; e < next; e++) {
                    bool all = alive[e];
                    for (int k = 0; k < group.PoolCount; k++) {
                        all &= group.GetPool(k).Has(e);
                    }
                    if (all) {
                        expected++;
                    }
                }
                if (group.Count != expected) {
                    errors.Add($"group {g}: Count {group.Count}, entities with every owned type {expected}");
                }
                for (int i = 0; i < group.Count; i++) {
                    for (int k = 0; k < group.PoolCount; k++) {
                        if (group.GetPool(k).RawEntities[i] != group.Entities[i]) {
                            errors.Add($"group {g}: pool {k} dense[{i}] is not aligned with the group");
                        }
                    }
                }
            }
            return errors;
        }

        private static bool Matches(World world, Filter filter, int e) {
            foreach (int t in filter.IncludedTypes) {
                if (world.GetPool(t)?.Has(e) != true) {
                    return false;
                }
            }
            foreach (int t in filter.ExcludedTypes) {
                if (world.GetPool(t)?.Has(e) == true) {
                    return false;
                }
            }
            if (filter.AnyTypes.Length == 0) {
                return true;
            }
            foreach (int t in filter.AnyTypes) {
                if (world.GetPool(t)?.Has(e) == true) {
                    return true;
                }
            }
            return false;
        }
    }
}
