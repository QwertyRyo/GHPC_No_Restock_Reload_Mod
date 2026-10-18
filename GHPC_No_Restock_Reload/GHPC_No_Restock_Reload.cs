using System;
using GHPC.Weapons;
using HarmonyLib;
using MelonLoader;
using System.Reflection;
using GHPC.Player;
using GHPC.AI;
using GHPC.Vehicle;
using GHPC;
using System.Collections.Generic;
[assembly: MelonInfo(typeof(GHPC_No_Restock_Reload_Mod.NoRestockReloadMod), "GHPC No Restock Reload", "1.0.0", "Qwertyryo")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace GHPC_No_Restock_Reload_Mod {
    public class NoRestockReloadMod : MelonMod {
        private static readonly System.Collections.Generic.Dictionary<AmmoFeed, float> storageReloadInProgress
            = new System.Collections.Generic.Dictionary<AmmoFeed, float>();

        private static MelonPreferences_Entry<float> storageReloadPenalty;
        public static float StorageReloadPenalty => storageReloadPenalty.Value;

        private static MelonPreferences_Entry<bool> considerStorageAmmo;
        public static bool ConsiderStorageAmmo => considerStorageAmmo.Value;

        public override void OnInitializeMelon() {
            var category = MelonPreferences.CreateCategory("NoRestockReload");
            storageReloadPenalty = category.CreateEntry("StorageReloadPenaltyMultiplier", 1.5f,
                description: "Reload time multiplier when loading from storage instead of the ready rack.");
            considerStorageAmmo = category.CreateEntry("ConsiderStorageAmmo", false,
                description: "Make AI consider storage ammo when selecting ammo type (checks if ammo exists in storage racks, not just ready rack).");

            HarmonyInstance.PatchAll();
        }

        public static bool IsStorageReloadInProgress(AmmoFeed feed) => storageReloadInProgress.ContainsKey(feed);
        public static void StartStorageReload(AmmoFeed feed, float penalty) => storageReloadInProgress[feed] = penalty;
        public static float EndStorageReload(AmmoFeed feed) {
            float original = storageReloadInProgress[feed];
            storageReloadInProgress.Remove(feed);
            return original;
        }
    }

    [HarmonyPatch(typeof(AmmoFeed), nameof(AmmoFeed.FeedNewClip))]
    static class Patch_FeedNewClip {
        [HarmonyPrefix]
        static bool Prefix(AmmoFeed __instance) {

            if (__instance.Reloading) {
                return false;
            }
            __instance._queuedClipTypeLockedIn = __instance.QueuedClipType;
            if (!__instance.ReadyRack.RackSafeToUse) {
                return false;
            }
            bool hasClip = __instance.ReadyRack.HasClipWithPattern(__instance._queuedClipTypeLockedIn);
            if (!hasClip) {
                // For ConsiderStorageAmmo, check if this tank is player-controlled
                // Only apply the restriction to AI tanks
                bool shouldAllowStorageReload = true;

                if (!NoRestockReloadMod.ConsiderStorageAmmo) {
                    // ConsiderStorageAmmo is disabled, so check if this is an AI tank
                    try {
                        var playerInput = PlayerInput.Instance;
                        if (playerInput != null) {
                            Unit playerUnit = playerInput.CurrentPlayerUnit;
                            if (playerUnit != null && typeof(AmmoFeed).GetField("_vehicle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) is Vehicle vehicle) {
                                // If this vehicle is NOT the player's unit, it's an AI tank
                                if (playerUnit != vehicle) {
                                    shouldAllowStorageReload = false;
                                }
                            }
                        }
                    } catch {
                        // If we can't determine, allow storage reload (assume player)
                    }

                    if (!shouldAllowStorageReload) {
                        return false;
                    }
                }

                int storageCount = 0;
                if (typeof(AmmoFeed).GetField("_loadoutManager", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) is LoadoutManager loadoutManager) {
                    foreach (var rackLoadout in loadoutManager.RackLoadouts) {
                        if (rackLoadout.Rack != null && rackLoadout.Rack != __instance.ReadyRack) {
                            storageCount += rackLoadout.Rack.GetCurrentStoredAmmoCount(__instance._queuedClipTypeLockedIn);
                        }
                    }
                }

                //fix if we ever get a storage rack with more than 50 rounds that is not an autocannon, or we get an autocannon with clips less than 50rd
                if (storageCount <= 0 || storageCount >= 50) {
                    return false;
                }

                __instance._clipFeedTime = 0f;
                __instance._clipFeedStage = 0;
                __instance.Reloading = true;

                if (__instance.Carousel != null) {
                    __instance.Carousel.SeekSlotWithClipType(__instance._queuedClipTypeLockedIn);
                }

                float penalty = NoRestockReloadMod.StorageReloadPenalty;
                NoRestockReloadMod.StartStorageReload(__instance, penalty);

                __instance.ResetLiveDurations();
                __instance.StartAutoloaderAudio();
                __instance.DoReloadStage(__instance.ClipReloadStages[0], true);
                foreach (var stage in __instance.ClipReloadStages) {
                    stage.LiveDuration *= penalty;
                }
                __instance.CalculateTotalTime(__instance.ClipReloadStages, false);
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(AmmoFeed), nameof(AmmoFeed.TryFinalFeedNewClip))]
    static class Patch_TryFinalFeedNewClip_StorageFallback {
        [HarmonyPrefix]
        static bool Prefix(AmmoFeed __instance, int forcedSlotIndex) {

            // Check if this is a storage reload
            bool isStorageReload = NoRestockReloadMod.IsStorageReloadInProgress(__instance);

            if (isStorageReload) {
                // Check if storage reload should be aborted based on ConsiderStorageAmmo
                bool shouldAbortStorageReload = false;

                if (!NoRestockReloadMod.ConsiderStorageAmmo) {
                    // ConsiderStorageAmmo is disabled, check if this is an AI tank
                    try {
                        var playerInput = PlayerInput.Instance;
                        if (playerInput != null) {
                            Unit playerUnit = playerInput.CurrentPlayerUnit;
                            if (playerUnit != null && typeof(AmmoFeed).GetField("_vehicle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) is Vehicle vehicle) {
                                // If this vehicle is NOT the player's unit, abort the storage reload
                                if (playerUnit != vehicle) {
                                    shouldAbortStorageReload = true;
                                }
                            }
                        }
                    } catch {
                        // If we can't determine, allow storage reload (assume player)
                    }

                    if (shouldAbortStorageReload) {
                        NoRestockReloadMod.EndStorageReload(__instance);
                        return true;
                    }
                }
            }

            if (isStorageReload) {
                NoRestockReloadMod.EndStorageReload(__instance);

                __instance.ResetLiveDurations();
                __instance._totalReloadTime = 0f;

                // Check if ready rack is safe
                if (!__instance.ReadyRack.RackSafeToUse) {
                    return false;
                }

                // Get LoadoutManager to check and decrement ammo
                if (typeof(AmmoFeed).GetField("_loadoutManager", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) is LoadoutManager lm) {
                    int storageCount = 0;
                    foreach (var rackLoadout in lm.RackLoadouts) {
                        if (rackLoadout.Rack != null && rackLoadout.Rack != __instance.ReadyRack) {
                            storageCount += rackLoadout.Rack.GetCurrentStoredAmmoCount(__instance._queuedClipTypeLockedIn);
                        }
                    }

                    if (storageCount <= 0) {
                        return false;
                    }

                    // Populate LoadedClip from the queued type
                    __instance.LoadedClip.Clear();
                    for (int i = 0; i < __instance._queuedClipTypeLockedIn.Capacity; i++) {
                        int num = i % __instance._queuedClipTypeLockedIn.MinimalPattern.Length;
                        __instance.LoadedClip.Enqueue(__instance._queuedClipTypeLockedIn.MinimalPattern[num].AmmoType);
                    }

                    var loadedNewClipDelegate = typeof(AmmoFeed).GetField("LoadedNewClip", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) as Action<AmmoType.AmmoClip>;
                    loadedNewClipDelegate?.Invoke(__instance._queuedClipTypeLockedIn);

                    __instance.LoadedClipType = __instance._queuedClipTypeLockedIn;

                    if (!__instance.ExclusivesChangeBeforeReload && (__instance.LoadedClipType == null || !__instance.LoadedClipType.Equals(__instance._queuedClipTypeLockedIn))) {
                        __instance.ToggleExclusiveItems(__instance._queuedClipTypeLockedIn);
                    }

                    if (__instance.AutoFeed && __instance.AmmoTypeInBreech == null) {
                        __instance.TryFinalFeedNewRound(false);
                    }

                    foreach (var rackLoadout in lm.RackLoadouts) {
                        if (rackLoadout.Rack != null && rackLoadout.Rack != __instance.ReadyRack) {
                            if (rackLoadout.Rack.GetCurrentStoredAmmoCount(__instance._queuedClipTypeLockedIn) > 0) {
                                rackLoadout.Rack.RetrieveClipWithPattern(__instance._queuedClipTypeLockedIn);
                                break;
                            }
                        }
                    }
                    return false; // Skip original
                } else {
                    return false;
                }
            }

            return true;
        }
    }


}


