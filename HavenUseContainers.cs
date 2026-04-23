// Requires: ItemRetriever

using Facepunch;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Haven Use Containers", "CathieNova", "1.0.0")]
    [Description("Allows players to craft and build using nearby containers.")]
    public class HavenUseContainers : RustPlugin
    {
        [PluginReference]
        private Plugin ItemRetriever;

        private const string UsePermission = "havenusecontainers.use";
        private const string AdminPermission = "havenusecontainers.admin";
        private const string ToolCupboardsPermission = "havenusecontainers.toolcupboards";
        private const string TeamContainersPermission = "havenusecontainers.teamcontainers";
        private const string FeedbackPermission = "havenusecontainers.feedback";
        private const string DebugPermission = "havenusecontainers.debug";
        private const string NearbyContainersSource = "NearbyContainers";
        private const string ToolCupboardsSource = "ToolCupboards";
        private const string ItemIdField = "ItemId";
        private const int DeployedLayerMask = Rust.Layers.Mask.Deployed;

        private StoredConfig _config;
        private bool _isRegistered;
        private Timer _refreshTimer;
        private readonly HashSet<ulong> _disabledPlayers = new();
        private readonly List<StorageContainer> _reusableNearbyContainers = new();
        private readonly List<BuildingPrivlidge> _reusableToolCupboards = new();
        private readonly Dictionary<int, int> _reusableTotals = new();
        private readonly Dictionary<int, bool> _itemAllowedCache = new();
        private readonly Dictionary<ulong, RefreshState> _refreshStateByPlayer = new();
        private readonly Dictionary<ulong, float> _nextRefreshAtByPlayer = new();
        private readonly Dictionary<ulong, PendingFeedback> _pendingFeedbackByPlayer = new();

        #region Hooks

        private void Init()
        {
            LoadConfigValues();
            RegisterPermissions();
            RegisterMessages();
        }

        private void OnServerInitialized()
        {
            RegisterWithItemRetriever();
            StartRefreshTimer();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name == "Item Retriever")
            {
                RegisterWithItemRetriever();
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name == "Item Retriever")
            {
                _isRegistered = false;
            }
        }

        private void Unload()
        {
            _refreshTimer?.Destroy();
            _refreshTimer = null;
            UnregisterFromItemRetriever();
            ClearPendingFeedback();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            _refreshStateByPlayer.Remove(player.userID);
            _disabledPlayers.Remove(player.userID);
            _nextRefreshAtByPlayer.Remove(player.userID);

            if (_pendingFeedbackByPlayer.TryGetValue(player.userID, out var pending))
            {
                pending.FlushTimer?.Destroy();
                _pendingFeedbackByPlayer.Remove(player.userID);
            }
        }

        #endregion

        #region Commands

        [ChatCommand("containers")]
        private void ContainersCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!HasUseAccess(player))
            {
                ReplyToPlayer(player, "NoUsePermission");
                return;
            }

            if (!_config.AllowPlayerToggle)
            {
                ReplyToPlayer(player, "ToggleDisabled");
                return;
            }

            if (_disabledPlayers.Contains(player.userID))
            {
                _disabledPlayers.Remove(player.userID);
                MarkInventoryDirty(player, true);
                ReplyToPlayer(player, "ContainersEnabled");
            }
            else
            {
                _disabledPlayers.Add(player.userID);
                MarkInventoryDirty(player, true);
                ReplyToPlayer(player, "ContainersDisabled");
            }
        }

        [ChatCommand("containersdebug")]
        private void ContainersDebugCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!CanUseDebug(player))
            {
                ReplyToPlayer(player, "NoDebugPermission");
                return;
            }

            var range = GetPlayerRange(player);

            GetNearbyContainers(player, _reusableNearbyContainers);
            var nearbyContainerCount = _reusableNearbyContainers.Count;
            _reusableNearbyContainers.Clear();

            GetNearbyToolCupboards(player, _reusableToolCupboards);
            var nearbyToolCupboardCount = _reusableToolCupboards.Count;
            _reusableToolCupboards.Clear();

            ReplyToPlayer(player, "DebugSummary",
                range.ToString("0.##", CultureInfo.InvariantCulture),
                nearbyContainerCount.ToString(CultureInfo.InvariantCulture),
                nearbyToolCupboardCount.ToString(CultureInfo.InvariantCulture));

            ReplyToPlayer(player, "DebugFlags",
                HasUseAccess(player).ToString(),
                HasTeamContainerAccess(player).ToString(),
                HasToolCupboardAccess(player).ToString());
        }

        #endregion

        #region Item Retriever

        private void RegisterWithItemRetriever()
        {
            if (_isRegistered)
                return;

            if (ItemRetriever == null)
            {
                PrintWarning("Item Retriever not found. Haven Use Containers will wait for it.");
                return;
            }

            var spec = new Dictionary<string, object>
            {
                ["Priority"] = _config.Priority,
                ["FindPlayerItems"] = new Action<BasePlayer, Dictionary<string, object>, List<Item>>(FindPlayerItems),
                ["SumPlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int>(SumPlayerItems),
                ["TakePlayerItemsV2"] = new Func<BasePlayer, Dictionary<string, object>, int, List<Item>, ItemCraftTask, int>(TakePlayerItems),
                ["SerializeForNetwork"] = new Action<BasePlayer, List<ProtoBuf.Item>>(SerializeForNetwork)
            };

            ItemRetriever.Call("API_AddSupplier", this, spec);
            _isRegistered = true;
            Puts("Registered with Item Retriever.");
        }

        private void UnregisterFromItemRetriever()
        {
            if (!_isRegistered || ItemRetriever == null)
                return;

            ItemRetriever.Call("API_RemoveSupplier", this);
            _isRegistered = false;
        }

        private void SerializeForNetwork(BasePlayer player, List<ProtoBuf.Item> collect)
        {
            if (player == null || collect == null)
                return;

            if (!CanUseNearbyContainers(player))
                return;

            _reusableTotals.Clear();
            AddSourceTotals(player, _reusableTotals);

            foreach (var entry in _reusableTotals)
            {
                if (entry.Value <= 0)
                    continue;

                var item = ItemManager.CreateByItemID(entry.Key, entry.Value);
                if (item == null)
                    continue;

                var itemData = item.Save();
                item.Remove();

                collect.Add(itemData);
            }

            _reusableTotals.Clear();
        }

        private void FindPlayerItems(BasePlayer player, Dictionary<string, object> itemQuery, List<Item> collect)
        {
            if (player == null || collect == null)
                return;

            if (!CanUseNearbyContainers(player))
                return;

            if (!TryGetItemId(itemQuery, out var itemId))
                return;

            if (!IsItemAllowed(itemId))
                return;

            for (var i = 0; i < _config.SourcePriority.Count; i++)
            {
                var source = _config.SourcePriority[i];

                if (source.Equals(NearbyContainersSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyContainers(player, _reusableNearbyContainers);

                    for (var j = 0; j < _reusableNearbyContainers.Count; j++)
                    {
                        var container = _reusableNearbyContainers[j];
                        if (container?.inventory?.itemList == null)
                            continue;

                        FindItems(container.inventory.itemList, itemId, collect);
                    }

                    _reusableNearbyContainers.Clear();
                }
                else if (source.Equals(ToolCupboardsSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyToolCupboards(player, _reusableToolCupboards);

                    for (var j = 0; j < _reusableToolCupboards.Count; j++)
                    {
                        var cupboard = _reusableToolCupboards[j];
                        if (cupboard?.inventory?.itemList == null)
                            continue;

                        FindItems(cupboard.inventory.itemList, itemId, collect);
                    }

                    _reusableToolCupboards.Clear();
                }
            }
        }

        private int SumPlayerItems(BasePlayer player, Dictionary<string, object> itemQuery)
        {
            if (player == null)
                return 0;

            if (!CanUseNearbyContainers(player))
                return 0;

            if (!TryGetItemId(itemQuery, out var itemId))
                return 0;

            if (!IsItemAllowed(itemId))
                return 0;

            var total = 0;

            for (var i = 0; i < _config.SourcePriority.Count; i++)
            {
                var source = _config.SourcePriority[i];

                if (source.Equals(NearbyContainersSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyContainers(player, _reusableNearbyContainers);

                    for (var j = 0; j < _reusableNearbyContainers.Count; j++)
                    {
                        var container = _reusableNearbyContainers[j];
                        if (container?.inventory?.itemList == null)
                            continue;

                        total += SumItems(container.inventory.itemList, itemId);
                    }

                    _reusableNearbyContainers.Clear();
                }
                else if (source.Equals(ToolCupboardsSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyToolCupboards(player, _reusableToolCupboards);

                    for (var j = 0; j < _reusableToolCupboards.Count; j++)
                    {
                        var cupboard = _reusableToolCupboards[j];
                        if (cupboard?.inventory?.itemList == null)
                            continue;

                        total += SumItems(cupboard.inventory.itemList, itemId);
                    }

                    _reusableToolCupboards.Clear();
                }
            }

            return total;
        }

        private int TakePlayerItems(BasePlayer player, Dictionary<string, object> itemQuery, int amount, List<Item> collect, ItemCraftTask task)
        {
            if (player == null || amount <= 0)
                return 0;

            if (!CanUseNearbyContainers(player))
                return 0;

            if (!TryGetItemId(itemQuery, out var itemId))
                return 0;

            if (!IsItemAllowed(itemId))
                return 0;

            var taken = 0;

            for (var i = 0; i < _config.SourcePriority.Count; i++)
            {
                if (taken >= amount)
                    break;

                var source = _config.SourcePriority[i];

                if (source.Equals(NearbyContainersSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyContainers(player, _reusableNearbyContainers);

                    for (var j = 0; j < _reusableNearbyContainers.Count; j++)
                    {
                        var container = _reusableNearbyContainers[j];
                        if (container?.inventory?.itemList == null)
                            continue;

                        taken += TakeItems(player, NearbyContainersSource, container.inventory.itemList, itemId, amount - taken, collect);

                        if (taken >= amount)
                            break;
                    }

                    _reusableNearbyContainers.Clear();
                }
                else if (source.Equals(ToolCupboardsSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyToolCupboards(player, _reusableToolCupboards);

                    for (var j = 0; j < _reusableToolCupboards.Count; j++)
                    {
                        var cupboard = _reusableToolCupboards[j];
                        if (cupboard?.inventory?.itemList == null)
                            continue;

                        taken += TakeItems(player, ToolCupboardsSource, cupboard.inventory.itemList, itemId, amount - taken, collect);

                        if (taken >= amount)
                            break;
                    }

                    _reusableToolCupboards.Clear();
                }
            }

            return taken;
        }

        #endregion

        #region Refresh

        private void StartRefreshTimer()
        {
            _refreshTimer?.Destroy();
            _refreshTimer = timer.Every(_config.RefreshIntervalSeconds, RefreshInventories);
        }

        private void RefreshInventories()
        {
            var players = BasePlayer.activePlayerList;
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || !player.IsConnected)
                    continue;

                if (!CanUseNearbyContainers(player))
                {
                    if (_refreshStateByPlayer.Remove(player.userID))
                    {
                        MarkInventoryDirty(player, true);
                    }

                    continue;
                }

                var newState = BuildRefreshState(player);

                if (_refreshStateByPlayer.TryGetValue(player.userID, out var oldState))
                {
                    if (!oldState.Equals(newState))
                    {
                        _refreshStateByPlayer[player.userID] = newState;
                        MarkInventoryDirty(player, false);
                    }
                }
                else
                {
                    _refreshStateByPlayer[player.userID] = newState;
                    MarkInventoryDirty(player, false);
                }
            }
        }

        private RefreshState BuildRefreshState(BasePlayer player)
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + Mathf.RoundToInt(player.transform.position.x * 2f);
                hash = hash * 31 + Mathf.RoundToInt(player.transform.position.y * 2f);
                hash = hash * 31 + Mathf.RoundToInt(player.transform.position.z * 2f);

                GetNearbyContainers(player, _reusableNearbyContainers);
                hash = hash * 31 + _reusableNearbyContainers.Count;

                for (var i = 0; i < _reusableNearbyContainers.Count; i++)
                {
                    var container = _reusableNearbyContainers[i];
                    if (container == null)
                        continue;

                    hash = hash * 31 + container.net.ID.GetHashCode();
                    hash = hash * 31 + GetContainerItemHash(container);
                }

                _reusableNearbyContainers.Clear();

                GetNearbyToolCupboards(player, _reusableToolCupboards);
                hash = hash * 31 + _reusableToolCupboards.Count;

                for (var i = 0; i < _reusableToolCupboards.Count; i++)
                {
                    var cupboard = _reusableToolCupboards[i];
                    if (cupboard == null)
                        continue;

                    hash = hash * 31 + cupboard.net.ID.GetHashCode();
                    hash = hash * 31 + GetContainerItemHash(cupboard);
                }

                _reusableToolCupboards.Clear();

                return new RefreshState(hash);
            }
        }

        private int GetContainerItemHash(StorageContainer container)
        {
            unchecked
            {
                var hash = 17;
                var itemList = container?.inventory?.itemList;
                if (itemList == null)
                    return hash;

                AddItemHash(itemList, ref hash);
                return hash;
            }
        }

        private void AddItemHash(List<Item> items, ref int hash)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                    continue;

                if (!IsItemAllowed(item.info.itemid))
                    continue;

                hash = hash * 31 + item.info.itemid;
                hash = hash * 31 + item.amount;

                if (item.contents?.itemList != null && item.contents.itemList.Count > 0)
                {
                    AddItemHash(item.contents.itemList, ref hash);
                }
            }
        }

        private void MarkInventoryDirty(BasePlayer player, bool force)
        {
            if (player?.inventory?.containerMain == null)
                return;

            var now = Time.realtimeSinceStartup;

            if (!force && _config.RefreshCooldownSeconds > 0f)
            {
                if (_nextRefreshAtByPlayer.TryGetValue(player.userID, out var nextRefreshAt) && nextRefreshAt > now)
                    return;

                _nextRefreshAtByPlayer[player.userID] = now + _config.RefreshCooldownSeconds;
            }

            player.inventory.containerMain.MarkDirty();
        }

        #endregion

        #region Container Search

        private bool CanUseNearbyContainers(BasePlayer player)
        {
            if (player == null)
                return false;

            if (!HasUseAccess(player))
                return false;

            if (_config.AllowPlayerToggle && _disabledPlayers.Contains(player.userID))
                return false;

            return true;
        }

        private bool HasUseAccess(BasePlayer player)
        {
            if (player == null)
                return false;

            return permission.UserHasPermission(player.UserIDString, UsePermission)
                || permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        private bool HasToolCupboardAccess(BasePlayer player)
        {
            if (player == null)
                return false;

            return permission.UserHasPermission(player.UserIDString, ToolCupboardsPermission)
                || permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        private bool HasTeamContainerAccess(BasePlayer player)
        {
            if (player == null)
                return false;

            return permission.UserHasPermission(player.UserIDString, TeamContainersPermission)
                || permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        private bool CanUseDebug(BasePlayer player)
        {
            if (player == null)
                return false;

            return permission.UserHasPermission(player.UserIDString, DebugPermission)
                || permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        private bool CanReceiveFeedback(BasePlayer player)
        {
            if (player == null)
                return false;

            if (!_config.SendChatFeedback)
                return false;

            return permission.UserHasPermission(player.UserIDString, FeedbackPermission)
                || permission.UserHasPermission(player.UserIDString, AdminPermission);
        }

        private float GetPlayerRange(BasePlayer player)
        {
            var range = _config.Range;

            if (player == null || _config.PermissionRanges == null)
                return range;

            foreach (var entry in _config.PermissionRanges)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                if (entry.Value <= 0f)
                    continue;

                if (permission.UserHasPermission(player.UserIDString, entry.Key) && entry.Value > range)
                {
                    range = entry.Value;
                }
            }

            return range;
        }

        private void GetNearbyContainers(BasePlayer player, List<StorageContainer> result)
        {
            result.Clear();

            if (!CanUseNearbyContainers(player))
                return;

            var range = GetPlayerRange(player);
            var entities = Pool.Get<List<BaseEntity>>();

            try
            {
                Vis.Entities(player.transform.position, range, entities, DeployedLayerMask);

                for (var i = 0; i < entities.Count; i++)
                {
                    var container = entities[i] as StorageContainer;
                    if (container == null)
                        continue;

                    if (container is BuildingPrivlidge)
                        continue;

                    if (container.IsDestroyed)
                        continue;

                    if (container.inventory == null)
                        continue;

                    if (!IsAllowedContainer(container))
                    {
                        DebugLog(player, $"Skipped {container.ShortPrefabName}: prefab not allowed.");
                        continue;
                    }

                    if (!CanUseContainer(player, container))
                        continue;

                    result.Add(container);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref entities);
            }

            result.Sort((a, b) =>
            {
                var aDistance = (a.transform.position - player.transform.position).sqrMagnitude;
                var bDistance = (b.transform.position - player.transform.position).sqrMagnitude;
                return aDistance.CompareTo(bDistance);
            });

            if (_config.MaxContainers > 0 && result.Count > _config.MaxContainers)
            {
                result.RemoveRange(_config.MaxContainers, result.Count - _config.MaxContainers);
            }
        }

        private void GetNearbyToolCupboards(BasePlayer player, List<BuildingPrivlidge> result)
        {
            result.Clear();

            if (!CanUseNearbyContainers(player))
                return;

            if (!_config.AllowToolCupboards)
                return;

            if (!HasToolCupboardAccess(player))
                return;

            var range = GetPlayerRange(player);
            var entities = Pool.Get<List<BaseEntity>>();

            try
            {
                Vis.Entities(player.transform.position, range, entities, DeployedLayerMask);

                for (var i = 0; i < entities.Count; i++)
                {
                    var cupboard = entities[i] as BuildingPrivlidge;
                    if (cupboard == null)
                        continue;

                    if (cupboard.IsDestroyed)
                        continue;

                    if (cupboard.inventory == null)
                        continue;

                    if (!CanUseToolCupboard(player, cupboard))
                        continue;

                    result.Add(cupboard);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref entities);
            }

            result.Sort((a, b) =>
            {
                var aDistance = (a.transform.position - player.transform.position).sqrMagnitude;
                var bDistance = (b.transform.position - player.transform.position).sqrMagnitude;
                return aDistance.CompareTo(bDistance);
            });

            if (_config.MaxToolCupboards > 0 && result.Count > _config.MaxToolCupboards)
            {
                result.RemoveRange(_config.MaxToolCupboards, result.Count - _config.MaxToolCupboards);
            }
        }

        private bool IsAllowedContainer(StorageContainer container)
        {
            if (_config.AllowedPrefabs.Count == 0)
                return true;

            return _config.AllowedPrefabs.Contains(container.ShortPrefabName);
        }

        private bool CanUseContainer(BasePlayer player, StorageContainer container)
        {
            if (player == null || container == null)
                return false;

            if (permission.UserHasPermission(player.UserIDString, AdminPermission) && _config.AdminBypassContainerRules)
                return true;

            if (_config.RequireUnlocked && IsLocked(container))
            {
                DebugLog(player, $"Skipped {container.ShortPrefabName}: locked.");
                return false;
            }

            if (_config.RequireBuildingPrivilegeAccess && !HasBuildingPrivilegeAccess(player, container))
            {
                DebugLog(player, $"Skipped {container.ShortPrefabName}: no building privilege access.");
                return false;
            }

            if (_config.AllowAllUnlockedContainers && !IsLocked(container))
                return true;

            if (container.OwnerID == 0)
                return _config.AllowOwnerlessContainers;

            if (container.OwnerID == player.userID)
                return true;

            if (_config.AllowTeamContainers && HasTeamContainerAccess(player) && AreTeammates(player.userID, container.OwnerID))
                return true;

            DebugLog(player, $"Skipped {container.ShortPrefabName}: owner rules.");
            return false;
        }

        private bool CanUseToolCupboard(BasePlayer player, BuildingPrivlidge cupboard)
        {
            if (player == null || cupboard == null)
                return false;

            if (permission.UserHasPermission(player.UserIDString, AdminPermission) && _config.AdminBypassContainerRules)
                return true;

            if (_config.RequireBuildingPrivilegeAccess && !cupboard.IsAuthed(player))
            {
                DebugLog(player, "Skipped tool cupboard: not authed.");
                return false;
            }

            if (cupboard.OwnerID == 0)
                return _config.AllowOwnerlessContainers;

            if (cupboard.OwnerID == player.userID)
                return true;

            if (_config.AllowTeamContainers && HasTeamContainerAccess(player) && AreTeammates(player.userID, cupboard.OwnerID))
                return true;

            return !_config.RequireBuildingPrivilegeAccess;
        }

        private bool HasBuildingPrivilegeAccess(BasePlayer player, StorageContainer container)
        {
            var privilege = container.GetBuildingPrivilege();
            if (privilege == null)
                return true;

            return privilege.IsAuthed(player);
        }

        private bool AreTeammates(ulong playerId, ulong ownerId)
        {
            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(playerId);
            if (team == null)
                return false;

            return team.members != null && team.members.Contains(ownerId);
        }

        private bool IsLocked(StorageContainer container)
        {
            var baseLock = container.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
            return baseLock != null && baseLock.IsLocked();
        }

        #endregion

        #region Item Search

        private void AddSourceTotals(BasePlayer player, Dictionary<int, int> totals)
        {
            for (var i = 0; i < _config.SourcePriority.Count; i++)
            {
                var source = _config.SourcePriority[i];

                if (source.Equals(NearbyContainersSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyContainers(player, _reusableNearbyContainers);

                    for (var j = 0; j < _reusableNearbyContainers.Count; j++)
                    {
                        var container = _reusableNearbyContainers[j];
                        if (container?.inventory?.itemList == null)
                            continue;

                        AddTotals(container.inventory.itemList, totals);
                    }

                    _reusableNearbyContainers.Clear();
                }
                else if (source.Equals(ToolCupboardsSource, StringComparison.OrdinalIgnoreCase))
                {
                    GetNearbyToolCupboards(player, _reusableToolCupboards);

                    for (var j = 0; j < _reusableToolCupboards.Count; j++)
                    {
                        var cupboard = _reusableToolCupboards[j];
                        if (cupboard?.inventory?.itemList == null)
                            continue;

                        AddTotals(cupboard.inventory.itemList, totals);
                    }

                    _reusableToolCupboards.Clear();
                }
            }
        }

        private void AddTotals(List<Item> items, Dictionary<int, int> totals)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                    continue;

                if (IsItemAllowed(item.info.itemid))
                {
                    if (totals.TryGetValue(item.info.itemid, out var amount))
                    {
                        totals[item.info.itemid] = amount + item.amount;
                    }
                    else
                    {
                        totals[item.info.itemid] = item.amount;
                    }
                }

                if (item.contents?.itemList != null && item.contents.itemList.Count > 0)
                {
                    AddTotals(item.contents.itemList, totals);
                }
            }
        }

        private void FindItems(List<Item> items, int itemId, List<Item> collect)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                    continue;

                if (item.info.itemid == itemId && IsItemAllowed(item.info.itemid))
                {
                    collect.Add(item);
                }

                if (item.contents?.itemList != null && item.contents.itemList.Count > 0)
                {
                    FindItems(item.contents.itemList, itemId, collect);
                }
            }
        }

        private int SumItems(List<Item> items, int itemId)
        {
            var total = 0;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                    continue;

                if (item.info.itemid == itemId && IsItemAllowed(item.info.itemid))
                {
                    total += item.amount;
                }

                if (item.contents?.itemList != null && item.contents.itemList.Count > 0)
                {
                    total += SumItems(item.contents.itemList, itemId);
                }
            }

            return total;
        }

        private int TakeItems(BasePlayer player, string sourceName, List<Item> items, int itemId, int amountToTake, List<Item> collect)
        {
            if (amountToTake <= 0)
                return 0;

            var taken = 0;

            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (taken >= amountToTake)
                    break;

                var item = items[i];
                if (item == null)
                    continue;

                if (item.contents?.itemList != null && item.contents.itemList.Count > 0)
                {
                    taken += TakeItems(player, sourceName, item.contents.itemList, itemId, amountToTake - taken, collect);

                    if (taken >= amountToTake)
                        break;
                }

                if (item.info.itemid != itemId)
                    continue;

                if (!IsItemAllowed(item.info.itemid))
                    continue;

                var remaining = amountToTake - taken;
                var amount = Math.Min(item.amount, remaining);

                if (amount <= 0)
                    continue;

                if (item.amount > amount)
                {
                    var splitItem = item.SplitItem(amount);
                    if (splitItem != null)
                    {
                        collect?.Add(splitItem);
                        RecordFeedback(player, sourceName, itemId, amount);
                        taken += amount;
                    }
                }
                else
                {
                    item.RemoveFromContainer();
                    collect?.Add(item);
                    RecordFeedback(player, sourceName, itemId, amount);
                    taken += amount;
                }
            }

            return taken;
        }

        private bool TryGetItemId(Dictionary<string, object> itemQuery, out int itemId)
        {
            itemId = 0;

            if (itemQuery == null)
                return false;

            if (!itemQuery.TryGetValue(ItemIdField, out var value) || value == null)
                return false;

            switch (value)
            {
                case int intValue:
                    itemId = intValue;
                    return true;
                case long longValue:
                    itemId = (int)longValue;
                    return true;
                case float floatValue:
                    itemId = (int)floatValue;
                    return true;
                case double doubleValue:
                    itemId = (int)doubleValue;
                    return true;
                case string stringValue:
                    return int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId);
                default:
                    return false;
            }
        }

        private bool IsItemAllowed(int itemId)
        {
            if (_itemAllowedCache.TryGetValue(itemId, out var isAllowed))
                return isAllowed;

            var definition = ItemManager.FindItemDefinition(itemId);
            var shortName = definition?.shortname ?? string.Empty;

            if (_config.ItemWhitelist.Count > 0)
            {
                isAllowed = _config.ItemWhitelist.Contains(shortName);
                _itemAllowedCache[itemId] = isAllowed;
                return isAllowed;
            }

            if (_config.ItemBlacklist.Count > 0 && _config.ItemBlacklist.Contains(shortName))
            {
                _itemAllowedCache[itemId] = false;
                return false;
            }

            _itemAllowedCache[itemId] = true;
            return true;
        }

        #endregion

        #region Feedback

        private void RecordFeedback(BasePlayer player, string sourceName, int itemId, int amount)
        {
            if (player == null)
                return;

            if (!CanReceiveFeedback(player))
                return;

            if (!_pendingFeedbackByPlayer.TryGetValue(player.userID, out var pending))
            {
                pending = new PendingFeedback();
                _pendingFeedbackByPlayer[player.userID] = pending;
            }

            pending.Add(sourceName, itemId, amount);

            if (pending.FlushTimer == null || pending.FlushTimer.Destroyed)
            {
                pending.FlushTimer = timer.Once(_config.FeedbackDelaySeconds, () => FlushFeedback(player.userID));
            }
        }

        private void FlushFeedback(ulong playerId)
        {
            if (!_pendingFeedbackByPlayer.TryGetValue(playerId, out var pending))
                return;

            _pendingFeedbackByPlayer.Remove(playerId);

            var player = BasePlayer.FindByID(playerId);
            if (player == null || !player.IsConnected)
                return;

            if (!CanReceiveFeedback(player))
                return;

            if (pending.Entries.Count == 0)
                return;

            var text = BuildFeedbackText(pending);
            if (!string.IsNullOrEmpty(text))
            {
                SendReply(player, text);
            }
        }

        private string BuildFeedbackText(PendingFeedback pending)
        {
            var builder = new StringBuilder();
            builder.Append(GetMessage("FeedbackPrefix"));
            builder.Append(" ");

            var firstEntry = true;
            foreach (var entry in pending.Entries)
            {
                if (!firstEntry)
                    builder.Append(" | ");

                firstEntry = false;

                builder.Append(GetSourceDisplayName(entry.Key));
                builder.Append(": ");

                var firstItem = true;
                foreach (var itemEntry in entry.Value)
                {
                    if (!firstItem)
                        builder.Append(", ");

                    firstItem = false;

                    var definition = ItemManager.FindItemDefinition(itemEntry.Key);
                    builder.Append(itemEntry.Value);
                    builder.Append(" ");
                    builder.Append(definition?.displayName?.english ?? definition?.shortname ?? itemEntry.Key.ToString(CultureInfo.InvariantCulture));
                }
            }

            builder.Append(".");
            return builder.ToString();
        }

        private string GetSourceDisplayName(string sourceName)
        {
            if (sourceName.Equals(ToolCupboardsSource, StringComparison.OrdinalIgnoreCase))
                return GetMessage("ToolCupboardsSource");

            return GetMessage("NearbyContainersSource");
        }

        private void ClearPendingFeedback()
        {
            foreach (var entry in _pendingFeedbackByPlayer)
            {
                entry.Value.FlushTimer?.Destroy();
            }

            _pendingFeedbackByPlayer.Clear();
        }

        #endregion

        #region Localization

        private void RegisterMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoUsePermission"] = "You don't have permission to use nearby containers.",
                ["ToggleDisabled"] = "Nearby containers are always enabled for you.",
                ["ContainersEnabled"] = "Nearby containers are now enabled.",
                ["ContainersDisabled"] = "Nearby containers are now disabled.",
                ["NoDebugPermission"] = "You don't have permission to use debug.",
                ["DebugSummary"] = "Range: {0}m | Containers: {1} | Tool cupboards: {2}",
                ["DebugFlags"] = "Use: {0} | Team containers: {1} | Tool cupboards: {2}",
                ["FeedbackPrefix"] = "Used",
                ["NearbyContainersSource"] = "nearby containers",
                ["ToolCupboardsSource"] = "tool cupboards"
            }, this);
        }

        private string GetMessage(string key, string playerId = null)
        {
            return lang.GetMessage(key, this, playerId);
        }

        private void ReplyToPlayer(BasePlayer player, string key, params object[] args)
        {
            var message = GetMessage(key, player?.UserIDString);
            if (args != null && args.Length > 0)
            {
                message = string.Format(message, args);
            }

            SendReply(player, message);
        }

        #endregion

        #region Debug

        private void DebugLog(BasePlayer player, string message)
        {
            if (!_config.EnableDebugLogging)
                return;

            if (player != null && CanUseDebug(player))
            {
                Puts($"[Haven Use Containers] {player.displayName}: {message}");
            }
        }

        #endregion

        #region Config

        private void RegisterPermissions()
        {
            permission.RegisterPermission(UsePermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(ToolCupboardsPermission, this);
            permission.RegisterPermission(TeamContainersPermission, this);
            permission.RegisterPermission(FeedbackPermission, this);
            permission.RegisterPermission(DebugPermission, this);

            if (_config?.PermissionRanges == null)
                return;

            foreach (var entry in _config.PermissionRanges)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                permission.RegisterPermission(entry.Key, this);
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = StoredConfig.CreateDefault();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            LoadConfigValues();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void LoadConfigValues()
        {
            try
            {
                _config = Config.ReadObject<StoredConfig>();
                if (_config == null)
                {
                    _config = StoredConfig.CreateDefault();
                }
            }
            catch
            {
                // Good Girls Action
                PrintWarning("Config file was invalid. Creating a new one.");
                _config = StoredConfig.CreateDefault();
            }

            _config.ApplyDefaults();
            _itemAllowedCache.Clear();
            SaveConfig();
        }

        private class StoredConfig
        {
            public float Range = 12f;
            public int MaxContainers = 24;
            public int MaxToolCupboards = 8;
            public int Priority = 100;
            public bool RequireUnlocked = true;
            public bool RequireBuildingPrivilegeAccess = true;
            public bool AllowToolCupboards = true;
            public bool AllowAllUnlockedContainers = false;
            public bool AllowOwnerlessContainers = false;
            public bool AllowTeamContainers = true;
            public bool AdminBypassContainerRules = true;
            public bool AllowPlayerToggle = true;
            public float RefreshIntervalSeconds = 5f;
            public float RefreshCooldownSeconds = 0.5f;
            public bool EnableDebugLogging = false;
            public bool SendChatFeedback = true;
            public float FeedbackDelaySeconds = 0.15f;
            public List<string> SourcePriority;
            public HashSet<string> ItemWhitelist = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ItemBlacklist = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AllowedPrefabs = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, float> PermissionRanges = new(StringComparer.OrdinalIgnoreCase)
            {
                ["havenusecontainers.range.16"] = 16f,
                ["havenusecontainers.range.24"] = 24f
            };

            public void ApplyDefaults()
            {
                if (Range <= 0f)
                    Range = 12f;

                if (MaxContainers < 0)
                    MaxContainers = 24;

                if (MaxToolCupboards < 0)
                    MaxToolCupboards = 8;

                if (RefreshIntervalSeconds <= 0f)
                    RefreshIntervalSeconds = 5f;

                if (RefreshCooldownSeconds < 0f)
                    RefreshCooldownSeconds = 0f;

                if (FeedbackDelaySeconds < 0f)
                    FeedbackDelaySeconds = 0f;

                if (SourcePriority == null || SourcePriority.Count == 0)
                {
                    SourcePriority = new List<string>
                    {
                        NearbyContainersSource,
                        ToolCupboardsSource
                    };
                }
                else
                {
                    var cleaned = new List<string>();

                    for (var i = 0; i < SourcePriority.Count; i++)
                    {
                        var source = SourcePriority[i];
                        if (string.IsNullOrWhiteSpace(source))
                            continue;

                        if (ContainsSource(cleaned, source))
                            continue;

                        if (!source.Equals(NearbyContainersSource, StringComparison.OrdinalIgnoreCase) &&
                            !source.Equals(ToolCupboardsSource, StringComparison.OrdinalIgnoreCase))
                            continue;

                        cleaned.Add(source);
                    }

                    if (cleaned.Count == 0)
                    {
                        cleaned.Add(NearbyContainersSource);
                        cleaned.Add(ToolCupboardsSource);
                    }

                    SourcePriority = cleaned;
                }

                if (ItemWhitelist == null)
                    ItemWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (ItemBlacklist == null)
                    ItemBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (AllowedPrefabs == null)
                    AllowedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (PermissionRanges == null)
                {
                    PermissionRanges = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["havenusecontainers.range.16"] = 16f,
                        ["havenusecontainers.range.24"] = 24f
                    };
                }
            }

            private bool ContainsSource(List<string> list, string source)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i].Equals(source, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }

            public static StoredConfig CreateDefault()
            {
                return new StoredConfig();
            }
        }

        #endregion

        #region Data

        private struct RefreshState
        {
            public int Hash;

            public RefreshState(int hash)
            {
                Hash = hash;
            }

            public override bool Equals(object obj)
            {
                return obj is RefreshState state && state.Hash == Hash;
            }

            public override int GetHashCode()
            {
                return Hash;
            }
        }

        private class PendingFeedback
        {
            public Timer FlushTimer;
            public readonly Dictionary<string, Dictionary<int, int>> Entries = new(StringComparer.OrdinalIgnoreCase);

            public void Add(string sourceName, int itemId, int amount)
            {
                if (!Entries.TryGetValue(sourceName, out var sourceItems))
                {
                    sourceItems = new Dictionary<int, int>();
                    Entries[sourceName] = sourceItems;
                }

                if (sourceItems.TryGetValue(itemId, out var currentAmount))
                {
                    sourceItems[itemId] = currentAmount + amount;
                }
                else
                {
                    sourceItems[itemId] = amount;
                }
            }
        }

        #endregion
    }
}
