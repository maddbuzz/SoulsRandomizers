﻿using System;
using System.Collections.Generic;
using System.Linq;
using static RandomizerCommon.AnnotationData;
using static RandomizerCommon.LocationData;
using static RandomizerCommon.Util;

namespace RandomizerCommon
{
    public class KeyItemsPermutation
    {
        private LocationData data;
        private AnnotationData ann;
        private bool explain;

        // Initial state
        private SortedSet<string> areas = new SortedSet<string>();
        private Dictionary<string, List<string>> combinedAreas = new Dictionary<string, List<string>>();
        private Dictionary<string, HashSet<string>> combinedWeights = new Dictionary<string, HashSet<string>>();
        private HashSet<string> unusedAreas = new HashSet<string>();
        private SortedSet<string> items = new SortedSet<string>();
        // Built up over item assignment
        private SortedDictionary<string, Node> nodes = new SortedDictionary<string, Node>();
        private Dictionary<string, HashSet<string>> loops = new Dictionary<string, HashSet<string>>();
        private Dictionary<string, HashSet<string>> itemEvents = new Dictionary<string, HashSet<string>>();

        public KeyItemsPermutation(RandomizerOptions options, LocationData data, AnnotationData ann, EnemyLocations enemies, bool explain)
        {
            this.data = data;
            this.ann = ann;
            this.explain = explain;

            Dictionary<string, bool> config = ann.GetConfig(options.GetLogicOptions());
            Dictionary<string, Expr> configExprs = config.ToDictionary(e => e.Key, e => e.Value ? Expr.TRUE : Expr.FALSE);

            Dictionary<LocationScope, (UniqueCategory, int)> counts = ann.GetUniqueCounts();
            Dictionary<string, Dictionary<UniqueCategory, int>> areaCounts = ann.AllAreas.ToDictionary(e => e.Key, e =>
            {
                Dictionary<UniqueCategory, int> dict = Node.EmptyCounts();
                foreach (LocationScope scope in e.Value.Where(s => counts.ContainsKey(s)))
                {
                    (UniqueCategory cat, int count) = counts[scope];
                    dict[cat] += count;
                }
                return dict;
            });

            Dictionary<string, string> equivalentGraph = new Dictionary<string, string>();
            void processDependencies(AreaAnnotation area, ISet<string> frees, bool assignItems)
            {
                string name = area.Name;
                HashSet<string> dependentAreas = new HashSet<string>();
                bool other = false;
                foreach (string free in frees)
                {
                    if (ann.Items.ContainsKey(free))
                    {
                        items.Add(free);
                        if (assignItems)
                        {
                            if (itemEvents.ContainsKey(free)) throw new Exception($"Internal error: {free} activates multiple events");
                            AddMulti(itemEvents, free, name);
                            if (area.AlwaysBefore != null)
                            {
                                AddMulti(itemEvents, free, area.AlwaysBefore);
                            }
                        }
                        other = true;
                    }
                    else if (ann.Areas.ContainsKey(free))
                    {
                        dependentAreas.Add(free);
                    }
                    else if (ann.Events.ContainsKey(free))
                    {
                        if (ann.EventAreas.TryGetValue(free, out string evArea))
                        {
                            dependentAreas.Add(evArea);
                        }
                        else
                        {
                            other = true;
                        }
                    }
                    else throw new Exception($"Internal error: Unknown dependency {free} in requirements for {area.Name}");
                }
                if (dependentAreas.Count == 1 && !other)
                {
                    equivalentGraph[name] = dependentAreas.First();
                    if (explain) Console.WriteLine($"Collapsed events for key item generation: {name} -> {frees.First()}");
                }
                // This is used for equivalence graph things. Should probably use this information in weight groups instead of actually combining the areas
                AddMulti(combinedAreas, name, name);
                // Weight base is used to specify that a key item, if placed in the base area, should also apply to this other area.
                AddMulti(combinedWeights, name, name);
                if (area.WeightBase != null)
                {
                    AddMulti(combinedWeights, area.WeightBase, name);
                }
            }
            foreach (AreaAnnotation ev in ann.Events.Values)
            {
                string name = ev.Name;
                Expr req = ev.ReqExpr.Substitute(configExprs).Simplify();
                if (req.IsFalse()) throw new Exception($"Internal error: event {ev} can't have no requirements");
                processDependencies(ev, req.FreeVars(), true);
                // Events are not dynamically placed anywhere, nor is anything placed inside of them, so they are always added to the graph upfront
                nodes[name] = new Node
                {
                    Name = name,
                    Req = req,
                    Counts = Node.EmptyCounts(),
                    CumKeyCount = -1,
                };
            }
            foreach (AreaAnnotation area in ann.Areas.Values)
            {
                string name = area.Name;
                Expr req = area.ReqExpr.Substitute(configExprs).Simplify();
                if (req.IsFalse())
                {
                    // Can happen with DLC
                    unusedAreas.Add(area.Name);
                    continue;
                }
                // Proper aliases are already represented using the BaseArea slot property, skip those
                if (ann.AreaAliases[name] != name) continue;
                processDependencies(area, req.FreeVars(), false);
                // This is where we used to skip combined areas in DS3, but now weight bases are added automatically
                nodes[name] = new Node
                {
                    Name = name,
                    Counts = areaCounts[name],
                    Req = req,
                    Weight = 1,
                    CumKeyCount = -1
                };
                areas.Add(name);
            }
            // Quick collapse of equivalence graph
            Dictionary<string, string> equivalent = new Dictionary<string, string>();
            string getBaseName(string name)
            {
                if (equivalent.ContainsKey(name))
                {
                    return equivalent[name];
                }
                else if (equivalentGraph.ContainsKey(name))
                {
                    string root = getBaseName(equivalentGraph[name]);
                    equivalent[name] = root;
                    AddMulti(combinedAreas, root, name);
                    return root;
                }
                else
                {
                    return name;
                }
            };
            foreach (KeyValuePair<string, string> equivalence in equivalentGraph)
            {
                getBaseName(equivalence.Key);
            }
            // TODO: Remove combinedAreas
            foreach (KeyValuePair<string, List<string>> entry in combinedAreas)
            {
                foreach (string alias in entry.Value)
                {
                    if (alias != entry.Key)
                    {
                        AddMulti(combinedWeights, entry.Key, alias);
                    }
                }
            }
            foreach (KeyValuePair<string, HashSet<string>> entry in combinedWeights.Where(e => e.Value.Count > 1).ToList())
            {
                foreach (string sharedArea in entry.Value.ToList())
                {
                    entry.Value.UnionWith(combinedWeights[sharedArea]);
                    combinedWeights[sharedArea] = entry.Value;
                }
            }
            if (explain)
            {
                HashSet<string> explained = new HashSet<string>();
                foreach (KeyValuePair<string, HashSet<string>> entry in combinedWeights)
                {
                    if (explained.Contains(entry.Key)) continue;
                    Console.WriteLine($"Combined group: [{string.Join(",", entry.Value)}]");
                    explained.UnionWith(entry.Value);
                }
            }

            // TODO: Make the dictionaries less of a slog to produce
            combinedAreas = combinedWeights.ToDictionary(e => e.Key, e => e.Value.ToList());

            // Last step - calculate rough measures of area difficulty, in terms of minimal number of items required for the area
            int getCumulativeCounts(string name)
            {
                Node node = nodes[name];
                if (node.CumKeyCount != -1)
                {
                    return node.KeyCount + node.CumKeyCount;
                }
                List<string> deps = node.Req.FreeVars().Where(free => areas.Contains(free) || ann.Events.ContainsKey(free)).ToList();
                int count = deps.Select(free => getCumulativeCounts(free)).DefaultIfEmpty().Max();
                node.CumKeyCount = count;
                return node.KeyCount + count;
            };
            foreach (Node node in nodes.Values)
            {
                getCumulativeCounts(node.Name);
                if (explain) Console.WriteLine($"{node.Name} ({node.Counts[UniqueCategory.KEY_SHOP]} shop / {node.KeyCount} area / ({node.Counts[UniqueCategory.QUEST_LOT]} quest / {node.CumKeyCount} cumulative): {node.Req}");
            }
        }

        public class Assignment
        {
            public readonly List<ItemKey> Priority = new List<ItemKey>();
            public readonly HashSet<string> RequiredEvents = new HashSet<string>();
            public readonly Dictionary<ItemKey, HashSet<string>> Assign = new Dictionary<ItemKey, HashSet<string>>();
            public readonly Dictionary<ItemKey, List<LocationScope>> RestrictedItems = new Dictionary<ItemKey, List<LocationScope>>();
            public readonly Dictionary<LocationScope, string> EffectiveLocation = new Dictionary<LocationScope, string>();
            public readonly Dictionary<string, double> LocationLateness = new Dictionary<string, double>();
            public readonly Dictionary<string, HashSet<string>> IncludedAreas = new Dictionary<string, HashSet<string>>();
        }

        public Assignment AssignItems(Random random, RandomizerOptions options, Preset preset)
        {
            List<string> itemOrder = new List<string>(items);

            // Right now, assign key items in a random order, with endgame items last.
            // We will get more devious runs from assigning later items later, may be worth looking into, especially for game with clear phases like Sekiro has.
            Shuffle(random, itemOrder);
            bool isEndgame(string i) => i.StartsWith("cinder") || i == "secretpassagekey";
            itemOrder = itemOrder.OrderBy(i => isEndgame(i) ? 1 : 0).ToList();

            // In race mode, always put shinobiprosthetic and younglordsbellcharm first, since there is only one ashinaoutskirts_template spot for key item placement logic
            if (ann.RaceModeItems.Count > 0)
            {
                itemOrder = itemOrder.OrderBy(i => i == "shinobiprosthetic" || i == "younglordsbellcharm" ? 0 : 1).ToList();
            }

            Assignment ret = new Assignment();
            // Find which events are required to access other things, and do not give them as much weight as dead ends with placement biases
            foreach (Node node in nodes.Values)
            {
                if (node.Req != null)
                {
                    ret.RequiredEvents.UnionWith(node.Req.FreeVars().Where(v => ann.Events.ContainsKey(v)));
                }
            }
            // First assign ashes for key item placement. Other quest assignments can happen later.
            // TODO: Make ashes its own system, rather than abusing quest system.
            if (ann.ItemGroups.ContainsKey("ashes"))
            {
                Dictionary<string, int> unmissableQuestSlots = new Dictionary<string, int>();
                List<string> ashesOrder = new List<string>(areas);
                ashesOrder = WeightedShuffle(random, ashesOrder, loc => Math.Min(nodes[loc].KeyCount - nodes[loc].Counts[UniqueCategory.KEY_SHOP], 3));
                int ashesIndex = 0;
                foreach (KeyValuePair<LocationScope, SlotAnnotation> entry in ann.Slots)
                {
                    LocationScope scope = entry.Key;
                    SlotAnnotation slot = entry.Value;
                    HashSet<string> tags = slot.GetTags();
                    // Unique item unlocks other unique items unconditionally - can add a location for key item. Mainly for ashes.
                    if (slot.QuestReqs != null && !slot.HasAnyTags(ann.NoKeyTags) && scope.UniqueId > 0 && slot.ItemReqs.Count == 1)
                    {
                        ItemKey key = slot.ItemReqs[0];
                        if (ret.Assign.ContainsKey(key)) throw new Exception($"Multiple assignments for {slot.QuestReqs}");
                        string loc = ashesOrder[ashesIndex++];
                        if (explain) Console.WriteLine($"Assigning key quest item {slot.QuestReqs} to {loc}");
                        int ashesCount = data.Location(scope).Count;
                        nodes[loc].AddShopCapacity(false, ashesCount);
                        nodes[loc].AddItem(false, false);
                        AddMulti(ret.Assign, key, combinedAreas[loc]);
                    }
                }
            }
            if (explain)
            {
                int raceModeCount = 0;
                foreach (string area in areas)
                {
                    int rmode = nodes[area].Count(false, true);
                    if (rmode > 0)
                    {
                        Console.WriteLine($"RACEMODE {area}: {rmode}");
                        raceModeCount += rmode;
                    }
                }
                Console.WriteLine($"TOTAL: {raceModeCount}");
            }

            Dictionary<string, string> forcemap = new Dictionary<string, string>();
            if (options["norandom"])
            {
                foreach (string item in itemOrder)
                {
                    ItemKey itemKey = ann.Items[item].Key;
                    ItemLocation loc = data.Data[itemKey].Locations.Values.First();
                    SlotAnnotation sn = ann.Slot(loc.LocScope);
                    forcemap[item] = sn.Area;
                }
            }
            else if (preset?.Items != null)
            {
                foreach (KeyValuePair<string, string> entry in preset.Items)
                {
                    forcemap[entry.Key] = entry.Value;
                }
            }

            // Assign key items
            bool debugChoices = false;
            float scaling = options.GetNum("keyitemchainweight");
            Dictionary<string, Expr> reqs = CollapseReqs();
            foreach (string item in itemOrder)
            {
                List<string> allowedAreas = areas.Where(a => !reqs[a].Needs(item)).ToList();
                if (debugChoices) Console.WriteLine($"{item} not allowed in areas: {string.Join(",", areas.Where(a => !allowedAreas.Contains(a)))}");
                bool redundant = allowedAreas.Count == areas.Count;
                HashSet<string> neededForEvent = itemEvents.TryGetValue(item, out HashSet<string> ev) ? ev : null;
                allowedAreas.RemoveAll(a => ann.Areas[a].Until != null && (neededForEvent == null || !neededForEvent.Contains(ann.Areas[a].Until)));

                float late = 0.1f;
                if (debugChoices) Console.WriteLine($"\n> Choices for {item}: " + string.Join(", ", allowedAreas.OrderBy(a => -Weight(a, lateFactor: late)).Select(a => $"{a} {Weight(a, lateFactor: late)}")));
                string selected = WeightedChoice(random, allowedAreas, a => Weight(a, lateFactor: late));

                if (forcemap.TryGetValue(item, out string forced))
                {
                    if (explain && !allowedAreas.Contains(forced)) Console.WriteLine($"Key item {item} put in non-random location {forced} which isn't normally allowed by logic");
                    selected = forced;
                }
                AddItem(item, selected, forced != null);

                ItemKey itemKey = ann.Items[item].Key;
                ret.Priority.Add(itemKey);
                ret.Assign[itemKey] = new HashSet<string> { selected };
                // Areas should include events there. Except for bell charm being dropped by chained ogre, if that option is enabled
                // todo: check this works okay with racemode key items, and nothing else randomized.
                if (!(item == "younglordsbellcharm" && options["earlyhirata"]))
                {
                    if (ann.AreaEvents.TryGetValue(selected, out List<string> events)) ret.Assign[itemKey].UnionWith(events);
                }
                if (explain) Console.WriteLine($"Adding {item} to {string.Join(",", ret.Assign[itemKey])}");

                // Update weights
                reqs = CollapseReqs();
                // If item was not really needed, don't update weighhts
                if (redundant) continue;
                // Heuristic which forms chains and spreads items across areas
                // Reduce weight for this area, and increase weight for areas which depend on the item
                AdjustWeight(selected, 1 / scaling);
                HashSet<string> addedAreas = new HashSet<string>();
                foreach (string area in areas)
                {
                    if (addedAreas.Contains(combinedWeights[area].First())) continue;
                    if (reqs[area].Needs(item))
                    {
                        AdjustWeight(area, scaling);
                        addedAreas.Add(combinedWeights[area].First());
                    }
                }
            }
            // The last placed item has the highest priority
            ret.Priority.Reverse();

            // Now that all key items have been assigned, determine which areas are blocked by other areas.
            // This is used to determine lateness within the game (by # of items encountered up to that point).
            Func<string, List<string>, HashSet<string>> getIncludedAreas = null;
            getIncludedAreas = (name, path) =>
            {
                path = path.Concat(new[] { name }).ToList();
                Node node = nodes[name];
                if (ret.IncludedAreas.ContainsKey(name))
                {
                    if (ret.IncludedAreas[name] == null)
                    {
                        throw new Exception($"Loop from {name} to {node.Req} - path {string.Join(",", path)}");
                    }
                    return ret.IncludedAreas[name];
                }
                ret.IncludedAreas[name] = null;
                HashSet<string> result = new HashSet<string>();
                if (areas.Contains(name) || ann.Events.ContainsKey(name))
                {
                    result.Add(name);
                }
                foreach (string free in node.Req.FreeVars())
                {
                    if (!(loops.ContainsKey(name) && loops[name].Contains(free)))
                    {
                        result.UnionWith(getIncludedAreas(free, path));
                    }
                }
                ret.IncludedAreas[name] = result;
                return result;
            };
            foreach (Node node in nodes.Values)
            {
                getIncludedAreas(node.Name, new List<string>());
                // Redefine weights for quest selection
                node.Weight = 1;
                if (areas.Contains(node.Name))
                {
                    node.CumKeyCount = ret.IncludedAreas[node.Name].Select(n => nodes[n].Count(true, true)).Sum();
                    if (explain && false) Console.WriteLine($"Quest area {node.Name}: {node.Count(true, true)}/{node.CumKeyCount}: {string.Join(",", ret.IncludedAreas[node.Name])}");
                }
            }
            // The above DFS adds both items and areas together, so remove the items.
            foreach (string key in ret.IncludedAreas.Keys.ToList())
            {
                if (!areas.Contains(key) && !ann.Events.ContainsKey(key)) ret.IncludedAreas.Remove(key);
            }
            foreach (string area in unusedAreas)
            {
                ret.IncludedAreas[area] = new HashSet<string>();
            }
            List<string> areaOrder = areas.OrderBy(a => nodes[a].CumKeyCount).ToList();
            Dictionary<string, int> areaIndex = Enumerable.Range(0, areaOrder.Count()).ToDictionary(i => areaOrder[i], i => i);
            string latestArea(IEnumerable<string> ns) {
                return areaOrder[ns.Select(n => areaIndex.TryGetValue(n, out int i) ? i : throw new Exception($"No order for area {n}")).DefaultIfEmpty().Max()];
            }

            if (explain && false)
            {
                foreach (var entry in ret.IncludedAreas) Console.WriteLine($"Area scope {entry.Key}: {string.Join(" ", entry.Value)}");
            }
            // The main difficult part is determining the effective area of locations. That requires assigning quest items to a location.
            SortedSet<string> questItems = new SortedSet<string>();
            // Dictionary for quest item -> area requiring quest item -> # of slots requiring quest item in that area. Used to customize eligible slots for placing the quest item.
            Dictionary<string, Dictionary<string, int>> questItemAreaSlots = new Dictionary<string, Dictionary<string, int>>();
            // TODO: Check that this works with ashes chaining - slot can't have ashes which transitively requires that slot.
            foreach (KeyValuePair<LocationScope, SlotAnnotation> entry in ann.Slots)
            {
                LocationScope scope = entry.Key;
                SlotAnnotation slot = entry.Value;
                string area = slot.GetArea();
                HashSet<string> tags = slot.GetTags();
                if (slot.QuestReqs != null)
                {
                    // Should likely use slot.ItemReqs. But strings are nice for debugging.
                    string[] questReqs = slot.QuestReqs.Split(' ');
                    foreach (string questReq in questReqs)
                    {
                        if (ann.Items.ContainsKey(questReq))
                        {
                            ItemKey itemKey = ann.Items[questReq].Key;
                            AddMulti(ret.RestrictedItems, itemKey, scope);
                            if (!questItemAreaSlots.ContainsKey(questReq))
                            {
                                questItemAreaSlots[questReq] = new Dictionary<string, int>();
                            }
                            questItemAreaSlots[questReq][area] = questItemAreaSlots[questReq].ContainsKey(area) ? questItemAreaSlots[questReq][area] + 1 : 1;
                            questItems.Add(questReq);
                        }
                    }
                }
                foreach (string tag in slot.TagList)
                {
                    if (tag.Contains(':'))
                    {
                        string[] parts = tag.Split(':');
                        if (parts[0] == "exclude")
                        {
                            // Console.WriteLine($"Adding exclude {ann.Items[parts[1]].Key} to {slot.Text}");
                            AddMulti(ret.RestrictedItems, ann.Items[parts[1]].Key, scope);
                        }
                    }
                }
            }
            // Assign quest items to areas.
            foreach (string questItem in questItems)
            {
                ItemKey itemKey = ann.Items[questItem].Key;
                if (ret.Assign.ContainsKey(itemKey))
                {
                    if (explain) Console.WriteLine($"{questItem} already assigned to {string.Join(", ", ret.Assign[itemKey])}");
                    continue;
                }
                Dictionary<string, int> questAreas = questItemAreaSlots[questItem];
                List<string> allowed = ann.ItemRestrict.ContainsKey(itemKey) ? areas.Intersect(ann.ItemRestrict[itemKey].Unique[0].AllowedAreas(ret.IncludedAreas)).ToList() : areas.ToList();
                string selected = WeightedChoice(random, allowed, a => Weight(a, true, 0.01f, questAreas.ContainsKey(a) ? questAreas[a] : 0));
                if (explain) Console.WriteLine($"Selecting {questItem} to go in {selected}");
                nodes[selected].AddItem(true, true);
                ret.Assign[itemKey] = new HashSet<string> { selected };
                if (ann.AreaEvents.TryGetValue(selected, out List<string> events)) ret.Assign[itemKey].UnionWith(events);
            }
            foreach (KeyValuePair<LocationScope, SlotAnnotation> entry in ann.Slots)
            {
                LocationScope scope = entry.Key;
                SlotAnnotation slot = entry.Value;
                HashSet<string> tags = slot.GetTags();
                if (slot.QuestReqs != null)
                {
                    // TODO: Worst case situation: There is a key item. There is an area, with slots, it gets assigned to. Each has a questreq pointing to another area.
                    // For key items, quest locations are not counted, except for upfront, where capacity is explicitly added.
                    // If it is a quest item instead, perhaps don't allow questreq locations to have quest items. They are just unstable. Stop abusing quests to do ashes.
                    string area = slot.GetArea();
                    if (unusedAreas.Contains(area))
                    {
                        continue;
                    }
                    // Set effective area if different from actual area. This is just used for placement heuristics
                    if (explain) Console.WriteLine($"For questreqs {slot.QuestReqs} - items {string.Join(",", slot.ItemReqs.SelectMany(item => ret.Assign[item]))}");
                    string effectiveArea = latestArea(Enumerable.Concat(slot.AreaReqs, slot.ItemReqs.SelectMany(item => ret.Assign[item].Where(a => areas.Contains(a)))));
                    if (area != effectiveArea) ret.EffectiveLocation[scope] = effectiveArea;
                }
            }
            int combinedTotal = areas.Select(a => nodes[a].CumKeyCount).Max();
            foreach (KeyValuePair<string, List<string>> entry in combinedAreas)
            {
                double partial = (double) nodes[entry.Key].CumKeyCount / combinedTotal;
                foreach (string same in entry.Value)
                {
                    ret.LocationLateness[same] = partial;
                }
            }
            return ret;
        }

        public float Weight(string area, bool allowQuest = false, float lateFactor = 0.1f, int removeQuest = 0)
        {
            Node node = nodes[area];
            int count = node.Count(allowQuest, true) - removeQuest;
            if (count == 0)
            {
                return 0;
            }
            count += (int)(node.CumKeyCount * lateFactor);
            return count * node.Weight;
        }

        public void AdjustWeight(string area, float factor)
        {
            if (factor == 1f) return;
            foreach (string sharedArea in combinedWeights[area])
            {
                nodes[sharedArea].Weight *= factor;
            }
        }

        public void AddItem(string item, string area, bool forced)
        {
            nodes[item] = new Node { Name = item, Req = Expr.Named(area) };
            if (forced) return;
            nodes[area].AddItem(allowQuest: false, allowShops: true);
        }

        // The core routine at the center of placing key items.
        // Given the current area layout and pending item assignment, reduce the condition for each area to be only in terms of items, not in terms of other areas.
        // Then, for a given item, it is possible to tell which areas unconditionally depend on that item. The item can then be placed anywhere else.
        private Dictionary<string, Expr> CollapseReqs()
        {
            Dictionary<string, bool> allDepsProcessed = new Dictionary<string, bool>();
            Action<List<string>, string> findLoops = null;
            findLoops = (path, name) =>
            {
                path = path.Concat(new[] { name }).ToList();
                if (allDepsProcessed.ContainsKey(name))
                {
                    // If all deps satisfied, no issue. Otherwise...
                    if (!allDepsProcessed[name])
                    {
                        List<string> subpath = path.Skip(path.IndexOf(name)).ToList();
                        // Use a heuristic to see where we should snip the path. This doesn't work in a very small portion
                        // of cases, but most valid randomizations are interesting so just try again with a different seed.
                        foreach ((string fro, string to) in subpath.Zip(subpath.Skip(1), (a, b) => (a, b)))
                        {
                            if (!nodes[fro].Req.Needs(to))
                            {
                                AddMulti(loops, fro, to);
                                return;
                            }
                        }
                        throw new Exception("Hard dependency loop");
                    }
                    return;
                }
                allDepsProcessed[name] = false;
                HashSet<string> nodeLoops = loops.ContainsKey(name) ? loops[name] : new HashSet<string>();
                foreach (string free in nodes[name].Req.FreeVars())
                {
                    if (nodes.ContainsKey(free) && !nodeLoops.Contains(free))
                    {
                        findLoops(path, free);
                    }
                }
                allDepsProcessed[name] = true;
            };
            foreach (string name in nodes.Keys)
            {
                findLoops(new List<string>(), name);
            }
            Dictionary<string, Expr> simplifiedReqs = new Dictionary<string, Expr>();
            Func<string, Expr> simplifyReqs = null;
            simplifyReqs = name =>
            {
                if (simplifiedReqs.ContainsKey(name))
                {
                    if (simplifiedReqs[name] == null)
                    {
                        throw new Exception($"Loop detection failed on {name} - internal error");
                    }
                    return simplifiedReqs[name];
                }
                simplifiedReqs[name] = null;
                Expr req = nodes[name].Req;
                // Delete loops
                if (loops.ContainsKey(name))
                {
                    req = req.Substitute(loops[name].ToDictionary(l => l, l => Expr.FALSE)).Simplify();
                }
                // Replace recursively
                req = req.Substitute(req.FreeVars()
                        .Where(free => nodes.ContainsKey(free))
                        .ToDictionary(free => free, free => simplifyReqs(free)))
                    .Simplify();
                simplifiedReqs[name] = req;
                return req;
            };
            foreach (string name in nodes.Keys)
            {
                simplifyReqs(name);
            }
            return simplifiedReqs;
        }

        public class Node
        {
            public string Name { get; set; }
            public Dictionary<UniqueCategory, int> Counts { get; set; }
            public Expr Req { get; set; }
            // Rough measure of difficulty - how many checks are available before getting to this point
            public int CumKeyCount { get; set; }
            public float Weight { get; set; }
            public void Merge(Dictionary<UniqueCategory, int> other)
            {
                foreach (UniqueCategory cat in Categories(true, true))
                {
                    Counts[cat] += other[cat];
                }
            }
            public int KeyCount { get => Count(false, true); }
            public int Count(bool allowQuest, bool allowShops)
            {
                return Categories(allowQuest, allowShops).Select(cat => Counts[cat]).Sum();
            }
            public void AddItem(bool allowQuest, bool allowShops)
            {
                foreach (UniqueCategory category in Categories(allowQuest, allowShops))
                {
                    if (Counts[category] > 0)
                    {
                        Counts[category]--;
                        return;
                    }
                }
                throw new Exception($"Cannot add item to {Name} in quest {allowQuest}, shops {allowShops}");
            }
            public void AddShopCapacity(bool allowQuest, int amount)
            {
                Counts[allowQuest ? UniqueCategory.QUEST_SHOP : UniqueCategory.KEY_SHOP] += amount;
            }
            public static IEnumerable<UniqueCategory> Categories(bool allowQuest, bool allowShops)
            {
                yield return UniqueCategory.KEY_LOT;
                if (allowShops) yield return UniqueCategory.KEY_SHOP;
                if (allowQuest)
                {
                    yield return UniqueCategory.QUEST_LOT;
                    if (allowShops) yield return UniqueCategory.QUEST_SHOP;
                }
            }
            public static Dictionary<UniqueCategory, int> EmptyCounts() => Categories(true, true).ToDictionary(c => c, c => 0);
        }
    }
}
