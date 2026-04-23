## Features
* Use nearby containers for crafting and building
* Optional tool cupboard support
* Lock, ownership, team, and tool cupboard auth checks
* Item whitelist and blacklist support
* Permission-based range overrides
* Chat feedback for used resources
* Debug command for testing
* Configurable source order for containers and tool cupboards

## Dependency
**Required:** [Item Retriever](https://umod.org/plugins/item-retriever)

## Permissions
* `havenusecontainers.use` 
* `havenusecontainers.admin` 
* `havenusecontainers.toolcupboards` 
* `havenusecontainers.teamcontainers` 
* `havenusecontainers.feedback` 
* `havenusecontainers.debug` 

Range permissions are set in the config, Example:

* `havenusecontainers.range.16` 
* `havenusecontainers.range.24` 

## Commands
* `/containers` 
  Toggle nearby container usage on or off

* `/containersdebug` 
  Show nearby source info for testing

## Configuration
```json
{
  "Range": 12.0,
  "MaxContainers": 24,
  "MaxToolCupboards": 8,
  "Priority": 100,
  "RequireUnlocked": true,
  "RequireBuildingPrivilegeAccess": true,
  "AllowToolCupboards": true,
  "AllowAllUnlockedContainers": false,
  "AllowOwnerlessContainers": false,
  "AllowTeamContainers": true,
  "AdminBypassContainerRules": true,
  "AllowPlayerToggle": true,
  "RefreshIntervalSeconds": 5.0,
  "RefreshCooldownSeconds": 0.5,
  "EnableDebugLogging": false,
  "SendChatFeedback": true,
  "FeedbackDelaySeconds": 0.15,
  "SourcePriority": [
    "NearbyContainers",
    "ToolCupboards"
  ],
  "ItemWhitelist": [
    "wood",
    "stones",
    "metal.fragments"
  ],
  "ItemBlacklist": [
    "scrap",
    "targeting.computer",
    "cctv.camera"
  ],
  "AllowedPrefabs": [
    "box.wooden.large",
    "woodbox_deployed",
    "fridge.deployed",
    "locker.deployed"
  ],
  "PermissionRanges": {
    "havenusecontainers.range.16": 16.0,
    "havenusecontainers.range.24": 24.0
  }
}
```

## Config Notes
**Range**
* Base range in meters used when no higher range permission applies.

**MaxContainers**
* Maximum number of nearby containers checked.

**MaxToolCupboards**
* Maximum number of nearby tool cupboards checked.

**Priority**
* Item Retriever supplier priority. Positive values run after normal inventory sources.

**RequireUnlocked**
* If true, locked containers cannot be used.

**RequireBuildingPrivilegeAccess**
* If true, players must have tool cupboard auth to use containers or tool cupboards in that privilege area.

**AllowToolCupboards**
* If true, nearby tool cupboards can be used as a resource source if the player has `havenusecontainers.toolcupboards` permission.

**AllowAllUnlockedContainers**
* If true, unlocked nearby containers can be used without ownership checks, as long as tool cupboard auth checks still pass.

**AllowOwnerlessContainers**
* If true, nearby containers with no owner can be used.

**AllowTeamContainers**
* If true, team-owned containers can be used if the player has `havenusecontainers.teamcontainers` permission.

**AdminBypassContainerRules**
* If true, players with `havenusecontainers.admin` permission bypass normal lock, ownership, team, and tool cupboard auth checks.

**AllowPlayerToggle**
* If true, players can enable or disable the plugin for themselves with `/containers` if they have `havenusecontainers.use` permission.

**RefreshIntervalSeconds**
* How often nearby sources are rechecked for crafting and building UI updates.

**RefreshCooldownSeconds**
* Minimum delay between inventory refresh updates per player.

**EnableDebugLogging**
* If true, debug information is written to the server console for players with `havenusecontainers.debug` permission.

**SendChatFeedback**
* If true, players with `havenusecontainers.feedback` permission get chat feedback showing what source supplied items.

**FeedbackDelaySeconds**
* Delay before chat feedback is grouped and sent.

**SourcePriority**
* Order used for this plugin's sources. Normal inventory is still checked before this.

**ItemWhitelist**
* Only these item shortnames can be used if the list is not empty. This overrides `ItemBlacklist`.

**ItemBlacklist**
* These item shortnames cannot be used when `ItemWhitelist` is empty.

**AllowedPrefabs**
* Limits usable nearby containers to these prefab shortnames. If empty, all supported nearby storage containers are allowed.

**PermissionRanges**
* Permission-based range overrides. If a player has more than one matching permission, the highest configured range is used.

## Support
If you want to support my work, you can donate [here](https://cathienova.net/donate/)