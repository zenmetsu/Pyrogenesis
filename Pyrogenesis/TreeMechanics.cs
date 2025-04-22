using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Pyrogenesis;
using Vintagestory.API.Client;

namespace Pyrogenesis
{
    public class TreeMechanics
    {
        private readonly ICoreServerAPI api;
        private readonly PyrogenesisConfig config;
        private readonly Dictionary<BlockPos, BurningBlock> burningBlocks = new();
        private readonly Dictionary<BlockPos, BlockPos> fireToLogMap = new();
        private readonly Dictionary<string, float> treeFellingCooldowns = new();
        private readonly HashSet<string> activeTrees = new();
        private readonly Dictionary<string, HashSet<BlockPos>> treeBlocksByTreeId = new();
        private readonly Dictionary<string, BlockPos> treeBaseByTreeId = new(); // Track base block for deduplication
        private const string MOD_ID = "pyrogenesis";
        private const float TREE_FELLING_COOLDOWN_SECONDS = 15f;
        private const float HIGHLIGHT_LINE_WIDTH = 1f;

        public TreeMechanics(ICoreServerAPI api, PyrogenesisConfig config)
        {
            this.api = api;
            this.config = config ?? new PyrogenesisConfig();
        }

        public Dictionary<BlockPos, BurningBlock> GetBurningBlocks() => burningBlocks;

        public void RemoveBurningBlock(BlockPos pos)
        {
            burningBlocks.Remove(pos);
            foreach (var treeEntry in treeBlocksByTreeId)
            {
                treeEntry.Value.Remove(pos);
            }
        }

        public void AddFireToLogMapping(BlockPos firePos, BlockPos logPos) => fireToLogMap[firePos] = logPos;

        public void RemoveFireToLogMapping(BlockPos firePos) => fireToLogMap.Remove(firePos);

        private bool IsTreeLog(string blockCode, PyrogenesisConfig config)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            config = config ?? new PyrogenesisConfig();
            return config.TreeLogPrefixes.Any(prefix => blockCode.StartsWith($"game:{prefix}-"));
        }

        private static bool IsTreeLeaf(string blockCode, PyrogenesisConfig config)
        {
            if (string.IsNullOrEmpty(blockCode)) return false;
            if (!blockCode.StartsWith("game:leaves-") && !blockCode.StartsWith("game:leavesbranchy-")) return false;
            if (new TreeMechanics(null, config).IsTreeLog(blockCode, config))
            {
                return false;
            }
            return true;
        }

        public (BlockPos Pos, int Id, string Name)? FindNearbyLog(IWorldAccessor world, BlockPos firePos)
        {
            for (int yOffset = 0; yOffset >= -3; yOffset--)
            {
                var checkPos = new BlockPos(firePos.X, firePos.Y + yOffset, firePos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode, config))
                {
                    var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                    return (basePos.Copy(), checkBlock.Id, checkBlockCode);
                }
            }

            for (int xOffset = -2; xOffset <= 2; xOffset++)
            {
                for (int yOffset = -2; yOffset <= 2; yOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(firePos.X + xOffset, firePos.Y + yOffset, firePos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLog(checkBlockCode, config))
                        {
                            var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                            return (basePos.Copy(), checkBlock.Id, checkBlockCode);
                        }
                    }
                }
            }

            for (int xOffset = -2; xOffset <= 2; xOffset++)
            {
                for (int yOffset = -2; yOffset <= 2; yOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(firePos.X + xOffset, firePos.Y + yOffset, firePos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlock != null && !string.IsNullOrEmpty(checkBlockCode) && IsTreeLeaf(checkBlockCode, config))
                        {
                            var logPos = FindTreeBaseFromLeaf(world, checkPos, checkBlockCode);
                            if (logPos != null)
                            {
                                var logBlock = world.BlockAccessor.GetBlock(logPos);
                                var logBlockCode = logBlock.Code?.ToString();
                                if (logBlockCode != null && IsTreeLog(logBlockCode, config))
                                {
                                    return (logPos.Copy(), logBlock.Id, logBlockCode);
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        private BlockPos FindTreeBase(IWorldAccessor world, BlockPos logPos, string logBlockCode)
        {
            var parts = logBlockCode.Split('-');
            string treeFellingGroupCode = parts.Length >= 3 ? parts[2].Split(':').Last() : null;
            if (treeFellingGroupCode == null)
            {
                return logPos;
            }

            var currentPos = logPos.Copy();
            var lowestPos = logPos.Copy();
            var candidates = new List<BlockPos> { lowestPos };

            for (int y = logPos.Y - 1; y >= 0; y--)
            {
                var checkPos = new BlockPos(logPos.X, y, logPos.Z);
                var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                var checkBlockCode = checkBlock?.Code?.ToString();
                if (checkBlockCode == null || !IsTreeLog(checkBlockCode, config)) break;

                string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                if (checkGroupCode == null || !checkGroupCode.EndsWith(treeFellingGroupCode))
                {
                    break;
                }

                lowestPos = checkPos.Copy();
                candidates.Add(lowestPos);
            }

            // For 2x2 trunks, check adjacent blocks at the lowest Y
            var baseY = candidates.Min(p => p.Y);
            var baseCandidates = candidates.Where(p => p.Y == baseY).ToList();
            foreach (var pos in candidates.Where(p => p.Y == baseY))
            {
                for (int xOffset = -1; xOffset <= 1; xOffset++)
                {
                    for (int zOffset = -1; zOffset <= 1; zOffset++)
                    {
                        if (xOffset == 0 && zOffset == 0) continue;
                        var checkPos = new BlockPos(pos.X + xOffset, pos.Y, pos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlockCode == null || !IsTreeLog(checkBlockCode, config)) continue;

                        string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                        if (checkGroupCode == treeFellingGroupCode)
                        {
                            baseCandidates.Add(checkPos.Copy());
                        }
                    }
                }
            }

            // Select northwest-most base block
            return baseCandidates.OrderBy(p => p.Z).ThenBy(p => p.X).First();
        }

        private BlockPos FindTreeBaseFromLeaf(IWorldAccessor world, BlockPos leafPos, string leafBlockCode)
        {
            string leafGroupCode = leafBlockCode.Split('-').Length >= 3 ? leafBlockCode.Split('-')[2].Split(':').Last() : null;
            if (leafGroupCode == null || !char.IsDigit(leafGroupCode[0]))
            {
                return null;
            }

            string treeFellingGroupCode = leafGroupCode.Substring(1);

            for (int y = leafPos.Y; y >= 0; y--)
            {
                for (int xOffset = -2; xOffset <= 2; xOffset++)
                {
                    for (int zOffset = -2; zOffset <= 2; zOffset++)
                    {
                        var checkPos = new BlockPos(leafPos.X + xOffset, y, leafPos.Z + zOffset);
                        var checkBlock = world.BlockAccessor.GetBlock(checkPos);
                        var checkBlockCode = checkBlock?.Code?.ToString();
                        if (checkBlockCode == null || !IsTreeLog(checkBlockCode, config)) continue;

                        string checkGroupCode = checkBlock.Attributes?["treeFellingGroupCode"].AsString();
                        if (checkGroupCode == treeFellingGroupCode)
                        {
                            var basePos = FindTreeBase(world, checkPos, checkBlockCode);
                            return basePos;
                        }
                    }
                }
            }

            return null;
        }

        private bool CanFellTree(BlockPos logPos, string treeId)
        {
            if (logPos == null || string.IsNullOrEmpty(treeId))
            {
                return false;
            }
            if (activeTrees.Contains(treeId))
            {
                return false;
            }
            if (treeFellingCooldowns.TryGetValue(treeId, out var lastFelledTime))
            {
                return api.World.ElapsedMilliseconds / 1000f - lastFelledTime >= TREE_FELLING_COOLDOWN_SECONDS;
            }
            return true;
        }

        private void MarkTreeFelled(BlockPos logPos, string treeId)
        {
            if (logPos != null && !string.IsNullOrEmpty(treeId))
            {
                treeFellingCooldowns[treeId] = api.World.ElapsedMilliseconds / 1000f;
                activeTrees.Add(treeId);
                api.World.RegisterCallback((dt) =>
                {
                    activeTrees.Remove(treeId);
                }, (int)(TREE_FELLING_COOLDOWN_SECONDS * 1000));
            }
        }

        public void TryFellTree(BlockPos firePos)
        {
            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree called for fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
            }
            if (firePos == null || !fireToLogMap.TryGetValue(firePos, out var logPos))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: No log mapping for fire at ({firePos.X}, {firePos.Y}, {firePos.Z})");
                }
                return;
            }

            var logBlock = api.World.BlockAccessor.GetBlock(logPos);
            var logBlockCode = logBlock.Code?.ToString();
            if (logBlockCode == null || !IsTreeLog(logBlockCode, config))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: Invalid log at ({logPos.X}, {logPos.Y}, {logPos.Z}), BlockCode: {logBlockCode}");
                }
                return;
            }

            string treeId = logBlock.Attributes?["treeFellingGroupCode"].AsString();
            if (string.IsNullOrEmpty(treeId) || !CanFellTree(logPos, treeId) || api.World == null)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: Invalid treeId ({treeId}) or cannot fell tree at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                }
                return;
            }

            // Check for duplicate tree by base block
            var basePos = FindTreeBase(api.World, logPos, logBlockCode);
            if (treeBaseByTreeId.TryGetValue(treeId, out var existingBase) && existingBase.Equals(basePos))
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree skipped: Tree {treeId} at base ({basePos.X}, {basePos.Y}, {basePos.Z}) already processed");
                }
                fireToLogMap.Remove(firePos);
                return;
            }

            var (treeBlocks, logPositions, leafPositions) = FindTree(api.World, logPos);
            if (treeBlocks.Count == 0)
            {
                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] TryFellTree failed: No tree blocks found for tree at ({logPos.X}, {logPos.Y}, {logPos.Z})");
                }
                return;
            }

            if (!treeBlocksByTreeId.ContainsKey(treeId))
            {
                treeBlocksByTreeId[treeId] = new HashSet<BlockPos>();
            }
            var treeBlockSet = treeBlocksByTreeId[treeId];
            foreach (var blockPos in treeBlocks)
            {
                treeBlockSet.Add(blockPos.Copy());
            }
            treeBaseByTreeId[treeId] = basePos.Copy();

            int logCount = 0;
            int leafCount = 0;
            var currentTime = api.World.ElapsedMilliseconds / 1000f;

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] leafPositions count: {leafPositions.Count}");
            }

            foreach (var blockPos in treeBlocks)
            {
                if (!burningBlocks.ContainsKey(blockPos))
                {
                    var block = api.World.BlockAccessor.GetBlock(blockPos);
                    var blockCode = block.Code?.ToString();
                    if (blockCode == null || block.Id == 0) continue;

                    int distanceFromBase = 0;
                    if (IsTreeLeaf(blockCode, config))
                    {
                        var leafTuple = leafPositions.Find(l => l.Pos.Equals(blockPos));
                        if (leafTuple.Pos != null)
                        {
                            distanceFromBase = leafTuple.DistanceFromBase;
                        }
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Leaf detected at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}, DistanceFromBase: {distanceFromBase}, TreeFellingGroupCode: {block.Attributes?["treeFellingGroupCode"].AsString() ?? "null"}");
                        }
                        leafCount++;
                    }
                    else if (IsTreeLog(blockCode, config))
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Log detected at ({blockPos.X}, {blockPos.Y}, {blockPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {block.Attributes?["treeFellingGroupCode"].AsString() ?? "null"}");
                        }
                        logCount++;
                    }

                    burningBlocks[blockPos.Copy()] = new BurningBlock(blockPos.Copy(), blockCode, currentTime, config, distanceFromBase);
                }
            }

            if (logCount > 0 || leafCount > 0)
            {
                int maxLeafDistance = leafPositions.Any() ? leafPositions.Max(l => l.DistanceFromBase) : 0;
                api.Logger.Notification($"[{MOD_ID}] [tree] Queued {logCount} new logs and {leafCount} new leaves for burning for tree {treeId} at ({logPos.X}, {logPos.Y}, {logPos.Z}), max leaf distance: {maxLeafDistance}");

                int groupId = (treeId + firePos.ToString()).GetHashCode();

                if (config.DebugMode)
                {
                    if (firePos != null)
                    {
                        var firePositions = new List<BlockPos> { firePos.Copy() };
                        var fireColors = new List<int> { unchecked((int)0xFFFF0000) }; // Red (ARGB)
                        foreach (var player in api.World.AllOnlinePlayers)
                        {
                            api.World.HighlightBlocks(player, groupId, firePositions, fireColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                        }
                    }

                    if (logPositions.Count > 0)
                    {
                        var logColors = Enumerable.Repeat(unchecked((int)0xFF0000FF), logPositions.Count).ToList(); // Blue (ARGB)
                        foreach (var player in api.World.AllOnlinePlayers)
                        {
                            api.World.HighlightBlocks(player, groupId, logPositions.Select(p => p.Copy()).ToList(), logColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                        }
                    }

                    if (leafPositions.Count > 0)
                    {
                        var leafColors = Enumerable.Repeat(unchecked((int)0xFF00FF00), leafPositions.Count).ToList(); // Lime Green (ARGB)
                        foreach (var player in api.World.AllOnlinePlayers)
                        {
                            api.World.HighlightBlocks(player, groupId, leafPositions.Select(l => l.Pos.Copy()).ToList(), leafColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube, HIGHLIGHT_LINE_WIDTH);
                        }
                    }
                }

                MarkTreeFelled(logPos, treeId);
            }

            fireToLogMap.Remove(firePos);
        }

        private (List<BlockPos> TreeBlocks, List<BlockPos> LogPositions, List<(BlockPos Pos, int DistanceFromBase)> LeafPositions) FindTree(IWorldAccessor world, BlockPos startPos)
        {
            if (world == null || startPos == null)
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            var startBlock = world.BlockAccessor.GetBlock(startPos);
            var startBlockCode = startBlock.Code?.ToString();
            if (startBlockCode == null || !IsTreeLog(startBlockCode, config))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            string treeFellingGroupCode = startBlock.Attributes?["treeFellingGroupCode"].AsString();
            int spreadIndex = NeedsSpreadIndex(startBlockCode) ? startBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;
            if (string.IsNullOrEmpty(treeFellingGroupCode))
            {
                return (new List<BlockPos>(), new List<BlockPos>(), new List<(BlockPos, int)>());
            }

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] Starting tree search at ({startPos.X}, {startPos.Y}, {startPos.Z}), TreeFellingGroupCode: {treeFellingGroupCode}, SpreadIndex: {spreadIndex}");
            }

            var queue = new Queue<(BlockPos Pos, int SpreadIndex, int Distance, BlockPos Parent)>();
            var leafQueue = new Queue<(BlockPos Pos, int SpreadIndex, string GroupCode, int Distance, BlockPos Parent)>();
            var visited = new HashSet<BlockPos>();
            var treeBlocks = new List<BlockPos>();
            var logPositions = new List<BlockPos>();
            var leafPositions = new List<(BlockPos Pos, int DistanceFromBase)>();
            var leafGroupCounts = new Dictionary<string, int>();

            queue.Enqueue((startPos.Copy(), spreadIndex, 0, null));
            // Do not add startPos to visited yet, to allow re-checking if needed

            while (queue.Count > 0)
            {
                var (currentPos, currentSpreadIndex, currentDistance, currentParent) = queue.Dequeue();
                var block = world.BlockAccessor.GetBlock(currentPos);
                var blockCode = block.Code?.ToString();

                if (blockCode == null || block.Id == 0) continue;

                string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                int nSpreadIndex = NeedsSpreadIndex(blockCode) ? block.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;

                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] Processing block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {ngCode ?? "null"}, SpreadIndex: {currentSpreadIndex}, Distance: {currentDistance}");
                }

                if (ngCode != null && (ngCode == treeFellingGroupCode || ngCode.EndsWith(treeFellingGroupCode)) && (nSpreadIndex <= currentSpreadIndex || !NeedsSpreadIndex(blockCode)))
                {
                    treeBlocks.Add(currentPos.Copy());
                    if (IsTreeLog(blockCode, config))
                    {
                        logPositions.Add(currentPos.Copy());
                    }

                    // Add to visited after processing to allow re-checking if needed
                    visited.Add(currentPos);

                    for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                    {
                        var facing = Vec3i.DirectAndIndirectNeighbours[i];
                        var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                        float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                        if (horDist > config.LeafSearchRadius)
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to LeafSearchRadius: horDist={horDist}, LeafSearchRadius={config.LeafSearchRadius}");
                            }
                            continue;
                        }
                        if (visited.Contains(neighborPos))
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) because already visited");
                            }
                            continue;
                        }

                        var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
                        var neighborCode = neighborBlock.Code?.ToString();
                        if (neighborCode == null || neighborBlock.Id == 0)
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to air or null block");
                            }
                            continue;
                        }

                        string nNgCode = neighborBlock.Attributes?["treeFellingGroupCode"].AsString();
                        int nNSpreadIndex = NeedsSpreadIndex(neighborCode) ? neighborBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;

                        string normalizedGroupCode = nNgCode ?? "";
                        if (IsTreeLeaf(neighborCode, config) && nNgCode != null)
                        {
                            // Handle both digit-prefixed and non-prefixed group codes
                            if (nNgCode.Length > 1 && char.IsDigit(nNgCode[0]))
                            {
                                normalizedGroupCode = nNgCode.Substring(1);
                            }
                            else
                            {
                                normalizedGroupCode = nNgCode;
                            }
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Normalized leaf group code at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) from {nNgCode} to {normalizedGroupCode}");
                            }
                        }

                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Checking neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, TreeFellingGroupCode: {nNgCode ?? "null"}, Normalized: {normalizedGroupCode}, SpreadIndex: {nNSpreadIndex}");
                        }

                        if (IsTreeLog(neighborCode, config) && nNgCode != null && (nNgCode == treeFellingGroupCode || nNgCode.EndsWith(treeFellingGroupCode)) && (nNSpreadIndex <= currentSpreadIndex || !NeedsSpreadIndex(neighborCode)))
                        {
                            queue.Enqueue((neighborPos.Copy(), nNSpreadIndex, currentDistance + 1, currentPos.Copy()));
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued log at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) with Distance: {currentDistance + 1}, TreeFellingGroupCode: {nNgCode}");
                            }
                        }
                        else if (IsTreeLeaf(neighborCode, config) && nNgCode != null && normalizedGroupCode == treeFellingGroupCode)
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Attempting to enqueue leaf at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, Original GroupCode: {nNgCode}, Normalized: {normalizedGroupCode}");
                            }
                            bool isConnected = IsConnectedToLog(world, neighborPos, new HashSet<BlockPos>(), treeFellingGroupCode, startPos);
                            if (!isConnected)
                            {
                                if (config.DebugMode)
                                {
                                    api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to lack of connectivity to log");
                                }
                                continue;
                            }
                            leafQueue.Enqueue((neighborPos.Copy(), nNSpreadIndex, nNgCode, currentDistance + 1, currentPos.Copy()));
                            leafGroupCounts[nNgCode] = leafGroupCounts.GetValueOrDefault(nNgCode, 0) + 1;
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued leaf at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) with DistanceFromBase: {currentDistance + 1}, TreeFellingGroupCode: {nNgCode}, Normalized: {normalizedGroupCode}");
                            }
                        }
                        else
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), IsLog: {IsTreeLog(neighborCode, config)}, IsLeaf: {IsTreeLeaf(neighborCode, config)}, GroupCodeMatch: {nNgCode != null && (nNgCode == treeFellingGroupCode || normalizedGroupCode == treeFellingGroupCode)}, SpreadIndexValid: {(nNSpreadIndex <= currentSpreadIndex || !NeedsSpreadIndex(neighborCode))}");
                            }
                        }
                    }
                }
            }

            while (leafQueue.Count > 0)
            {
                var (currentPos, currentSpreadIndex, currentGroupCode, currentDistance, currentParent) = leafQueue.Dequeue();
                var block = world.BlockAccessor.GetBlock(currentPos);
                var blockCode = block.Code?.ToString();

                if (blockCode == null || block.Id == 0) continue;

                string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                string normalizedGroupCode = ngCode ?? "";
                if (IsTreeLeaf(blockCode, config) && ngCode != null)
                {
                    if (ngCode.Length > 1 && char.IsDigit(ngCode[0]))
                    {
                        normalizedGroupCode = ngCode.Substring(1);
                    }
                    else
                    {
                        normalizedGroupCode = ngCode;
                    }
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Normalized leaf group code at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}) from {ngCode} to {normalizedGroupCode}");
                    }
                }

                if (config.DebugMode)
                {
                    api.Logger.Debug($"[{MOD_ID}] [tree] Processing leaf queue block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {ngCode ?? "null"}, Normalized: {normalizedGroupCode}, Distance: {currentDistance}");
                }

                if (ngCode != null && normalizedGroupCode == treeFellingGroupCode)
                {
                    treeBlocks.Add(currentPos.Copy());
                    if (IsTreeLeaf(blockCode, config))
                    {
                        leafPositions.Add((currentPos.Copy(), currentDistance));
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Added leaf to leafPositions at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}) with DistanceFromBase: {currentDistance}, TreeFellingGroupCode: {ngCode}, Normalized: {normalizedGroupCode}");
                        }
                    }

                    // Add to visited after processing to allow re-checking
                    visited.Add(currentPos);

                    for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                    {
                        var facing = Vec3i.DirectAndIndirectNeighbours[i];
                        var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                        float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                        if (horDist > config.LeafSearchRadius)
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to LeafSearchRadius: horDist={horDist}, LeafSearchRadius={config.LeafSearchRadius}");
                            }
                            continue;
                        }
                        if (visited.Contains(neighborPos))
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) because already visited");
                            }
                            continue;
                        }

                        var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
                        var neighborCode = neighborBlock.Code?.ToString();
                        if (neighborCode == null || neighborBlock.Id == 0)
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to air or null block");
                            }
                            continue;
                        }

                        string nNgCode = neighborBlock.Attributes?["treeFellingGroupCode"].AsString();
                        int nNSpreadIndex = NeedsSpreadIndex(neighborCode) ? neighborBlock.Attributes?["treeFellingGroupSpreadIndex"].AsInt(0) ?? 0 : -1;

                        string neighborNormalizedGroupCode = nNgCode ?? "";
                        if (IsTreeLeaf(neighborCode, config) && nNgCode != null)
                        {
                            if (nNgCode.Length > 1 && char.IsDigit(nNgCode[0]))
                            {
                                neighborNormalizedGroupCode = nNgCode.Substring(1);
                            }
                            else
                            {
                                neighborNormalizedGroupCode = nNgCode;
                            }
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Normalized leaf group code at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) from {nNgCode} to {neighborNormalizedGroupCode}");
                            }
                        }

                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Checking leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, TreeFellingGroupCode: {nNgCode ?? "null"}, Normalized: {neighborNormalizedGroupCode}, SpreadIndex: {nNSpreadIndex}");
                        }

                        if (nNgCode != null && neighborNormalizedGroupCode == treeFellingGroupCode)
                        {
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Attempting to enqueue leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, Original GroupCode: {nNgCode}, Normalized: {neighborNormalizedGroupCode}");
                            }
                            bool isConnected = IsConnectedToLog(world, neighborPos, new HashSet<BlockPos>(), treeFellingGroupCode, startPos);
                            if (!isConnected)
                            {
                                if (config.DebugMode)
                                {
                                    api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to lack of connectivity to log");
                                }
                                continue;
                            }
                            leafQueue.Enqueue((neighborPos.Copy(), nNSpreadIndex, nNgCode, currentDistance + 1, currentPos.Copy()));
                            leafGroupCounts[nNgCode] = leafGroupCounts.GetValueOrDefault(nNgCode, 0) + 1;
                            if (config.DebugMode)
                            {
                                api.Logger.Debug($"[{MOD_ID}] [tree] Enqueued leaf neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) with DistanceFromBase: {currentDistance + 1}, TreeFellingGroupCode: {nNgCode}, Normalized: {neighborNormalizedGroupCode}");
                            }
                        }
                    }
                }
                else
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Skipped leaf queue block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), GroupCodeMatch: {ngCode != null && normalizedGroupCode == treeFellingGroupCode}");
                    }
                }
            }

            return (treeBlocks, logPositions, leafPositions);
        }

        private bool IsConnectedToLog(IWorldAccessor world, BlockPos leafPos, HashSet<BlockPos> initialVisited, string treeFellingGroupCode, BlockPos startPos)
        {
            var queue = new Queue<(BlockPos Pos, BlockPos Parent)>();
            var localVisited = new HashSet<BlockPos>(initialVisited);
            queue.Enqueue((leafPos.Copy(), null));
            localVisited.Add(leafPos);

            while (queue.Count > 0)
            {
                var (currentPos, currentParent) = queue.Dequeue();
                var block = world.BlockAccessor.GetBlock(currentPos);
                var blockCode = block.Code?.ToString();

                if (blockCode == null || block.Id == 0)
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check skipped block at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}) due to air or null block");
                    }
                    continue;
                }

                string ngCode = block.Attributes?["treeFellingGroupCode"].AsString();
                string normalizedGroupCode = ngCode ?? "";
                if (IsTreeLeaf(blockCode, config) && ngCode != null)
                {
                    if (ngCode.Length > 1 && char.IsDigit(ngCode[0]))
                    {
                        normalizedGroupCode = ngCode.Substring(1);
                    }
                    else
                    {
                        normalizedGroupCode = ngCode;
                    }
                }

                if (IsTreeLog(blockCode, config) && ngCode != null && (ngCode == treeFellingGroupCode || ngCode.EndsWith(treeFellingGroupCode)))
                {
                    if (config.DebugMode)
                    {
                        api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check found log at ({currentPos.X}, {currentPos.Y}, {currentPos.Z}), BlockCode: {blockCode}, TreeFellingGroupCode: {ngCode}");
                    }
                    return true;
                }

                for (int i = 0; i < Vec3i.DirectAndIndirectNeighbours.Length; i++)
                {
                    var facing = Vec3i.DirectAndIndirectNeighbours[i];
                    var neighborPos = new BlockPos(currentPos.X + facing.X, currentPos.Y + facing.Y, currentPos.Z + facing.Z);

                    float horDist = GameMath.Sqrt(neighborPos.HorDistanceSqTo(startPos.X, startPos.Z));
                    if (horDist > config.LeafSearchRadius)
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to LeafSearchRadius: horDist={horDist}, LeafSearchRadius={config.LeafSearchRadius}");
                        }
                        continue;
                    }
                    if (localVisited.Contains(neighborPos))
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) because already visited");
                        }
                        continue;
                    }

                    var neighborBlock = world.BlockAccessor.GetBlock(neighborPos);
                    var neighborCode = neighborBlock.Code?.ToString();
                    if (neighborCode == null || neighborBlock.Id == 0)
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check skipped neighbor at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) due to air or null block");
                        }
                        continue;
                    }

                    string nNgCode = neighborBlock.Attributes?["treeFellingGroupCode"].AsString();
                    string neighborNormalizedGroupCode = nNgCode ?? "";
                    if (IsTreeLeaf(neighborCode, config) && nNgCode != null)
                    {
                        if (nNgCode.Length > 1 && char.IsDigit(nNgCode[0]))
                        {
                            neighborNormalizedGroupCode = nNgCode.Substring(1);
                        }
                        else
                        {
                            neighborNormalizedGroupCode = nNgCode;
                        }
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check normalized group code at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}) from {nNgCode} to {neighborNormalizedGroupCode}");
                        }
                    }

                    if ((IsTreeLog(neighborCode, config) || IsTreeLeaf(neighborCode, config)) && nNgCode != null && neighborNormalizedGroupCode == treeFellingGroupCode)
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check enqueued block at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode}, TreeFellingGroupCode: {nNgCode}, Normalized: {neighborNormalizedGroupCode}");
                        }
                        queue.Enqueue((neighborPos.Copy(), currentPos.Copy()));
                        localVisited.Add(neighborPos);
                    }
                    else
                    {
                        if (config.DebugMode)
                        {
                            api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check skipped block at ({neighborPos.X}, {neighborPos.Y}, {neighborPos.Z}), BlockCode: {neighborCode ?? "null"}, TreeFellingGroupCode: {nNgCode ?? "null"}, Normalized: {neighborNormalizedGroupCode}, IsLog: {IsTreeLog(neighborCode, config)}, IsLeaf: {IsTreeLeaf(neighborCode, config)}");
                        }
                    }
                }
            }

            if (config.DebugMode)
            {
                api.Logger.Debug($"[{MOD_ID}] [tree] Connectivity check for leaf at ({leafPos.X}, {leafPos.Y}, {leafPos.Z}) failed to find a log");
            }
            return false;
        }

        private bool NeedsSpreadIndex(string blockCode)
        {
            return blockCode.StartsWith("game:logsection-");
        }
    }
}