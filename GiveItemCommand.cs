using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Data;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    internal static class GiveItemCommand
    {
        private const int MaxSuggestions = 10;

        private sealed class SearchEntry
        {
            internal SpawnableCrate Crate = null!;
            internal string Name = string.Empty;
            internal string Barcode = string.Empty;
            internal string Pallet = string.Empty;
            internal string Author = string.Empty;
            internal string Description = string.Empty;
        }

        internal static bool TrySpawnByQuery(string query)
        {
            string trimmedQuery = (query ?? string.Empty).Trim();
            if (trimmedQuery.Length == 0)
            {
                MelonLogger.Warning("[Void Engine] Give Item needs a name, for example: function_giveitem: M870");
                return false;
            }

            string normalizedQuery = CatalogSearchText.Normalize(trimmedQuery);
            if (normalizedQuery.Length == 0)
            {
                MelonLogger.Warning("[Void Engine] Item name must contain letters or numbers.");
                return false;
            }

            if (!TryLoadCatalog(out List<SearchEntry> catalog))
                return false;

            SearchEntry? exactMatch = catalog
                .Where(entry => string.Equals(entry.Name, normalizedQuery, StringComparison.Ordinal))
                .OrderBy(entry => entry.Crate.name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            exactMatch ??= catalog
                .Where(entry => string.Equals(entry.Barcode, normalizedQuery, StringComparison.Ordinal))
                .OrderBy(entry => entry.Crate.name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (exactMatch != null)
                return Spawn(exactMatch.Crate);

            List<SearchEntry> suggestions = GetSuggestions(catalog, normalizedQuery);
            if (suggestions.Count == 0)
            {
                MelonLogger.Warning(
                    $"[Void Engine] No exact spawnable matched '{trimmedQuery}', and no similar items were found."
                );
                return false;
            }

            MelonLogger.Warning(
                $"[Void Engine] '{trimmedQuery}' is not an exact item name. Copy a suggestion below and enter it as function_giveitem: <name>."
            );
            for (int i = 0; i < suggestions.Count; i++)
            {
                SearchEntry entry = suggestions[i];
                MelonLogger.Msg(
                    $"[Void Engine] Item suggestion {i + 1}: {entry.Crate.name} " +
                    $"(barcode: {entry.Crate.Barcode?.ID ?? "unknown"})"
                );
            }

            return false;
        }

        private static bool TryLoadCatalog(out List<SearchEntry> catalog)
        {
            catalog = new List<SearchEntry>();
            try
            {
                var crates = AssetWarehouse.Instance.GetCrates<SpawnableCrate>(
                    (ICrateFilter<SpawnableCrate>)null!
                );

                HashSet<string> seenBarcodes = new(StringComparer.OrdinalIgnoreCase);
                foreach (SpawnableCrate? crate in crates)
                {
                    if (crate == null || crate.Barcode == null || string.IsNullOrWhiteSpace(crate.Barcode.ID))
                        continue;

                    if (seenBarcodes.Add(crate.Barcode.ID))
                        catalog.Add(CreateEntry(crate));
                }

                if (catalog.Count == 0)
                {
                    MelonLogger.Warning("[Void Engine] The spawnable item catalogue is empty or not ready.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not search the spawnable item catalogue: {ex.GetBaseException().Message}"
                );
                return false;
            }
        }

        private static SearchEntry CreateEntry(SpawnableCrate crate)
        {
            string barcode = crate.Barcode != null ? crate.Barcode.ID : string.Empty;
            string pallet = crate._pallet != null ? crate._pallet.name : string.Empty;
            string author = crate._pallet != null ? crate._pallet.Author : string.Empty;
            return new SearchEntry
            {
                Crate = crate,
                Name = CatalogSearchText.Normalize(crate.name),
                Barcode = CatalogSearchText.Normalize(barcode),
                Pallet = CatalogSearchText.Normalize(pallet),
                Author = CatalogSearchText.Normalize(author),
                Description = CatalogSearchText.Normalize(crate._description ?? string.Empty)
            };
        }

        private static List<SearchEntry> GetSuggestions(List<SearchEntry> catalog, string normalizedQuery)
        {
            List<SearchEntry> matchingEntries = catalog
                .Where(entry => Matches(entry, normalizedQuery))
                .OrderBy(entry => Relevance(entry, normalizedQuery))
                .ThenBy(entry => entry.Crate.name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)
                .ToList();

            if (matchingEntries.Count > 0)
                return matchingEntries;

            int maxDistance = CatalogSearchText.SimilarityThreshold(normalizedQuery);
            return catalog
                .Select(entry => new
                {
                    Entry = entry,
                    Distance = CatalogSearchText.LevenshteinDistance(entry.Name, normalizedQuery)
                })
                .Where(candidate => candidate.Distance <= maxDistance)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Entry.Crate.name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)
                .Select(candidate => candidate.Entry)
                .ToList();
        }

        private static bool Matches(SearchEntry entry, string normalizedQuery)
        {
            return entry.Name.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Barcode.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Pallet.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Author.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Description.Contains(normalizedQuery, StringComparison.Ordinal);
        }

        private static int Relevance(SearchEntry entry, string normalizedQuery)
        {
            if (string.Equals(entry.Name, normalizedQuery, StringComparison.Ordinal))
                return 0;
            if (entry.Name.StartsWith(normalizedQuery, StringComparison.Ordinal))
                return 1;
            if (entry.Name.Contains(normalizedQuery, StringComparison.Ordinal))
                return 2;
            return 3;
        }

        private static bool Spawn(SpawnableCrate crate)
        {
            try
            {
                if (crate == null || crate.Barcode == null || string.IsNullOrWhiteSpace(crate.Barcode.ID))
                {
                    MelonLogger.Warning("[Void Engine] Could not spawn the selected item: its barcode is missing.");
                    return false;
                }

                var rig = Player.RigManager;
                if (rig == null)
                {
                    MelonLogger.Warning("[Void Engine] Could not spawn the item: the player rig is not ready.");
                    return false;
                }

                Camera? playerCamera = Camera.main;
                Transform view = playerCamera != null ? playerCamera.transform : rig.transform;
                Vector3 forward = Vector3.ProjectOnPlane(view.forward, Vector3.up);
                if (forward.sqrMagnitude < 0.001f)
                    forward = Vector3.ProjectOnPlane(rig.transform.forward, Vector3.up);
                if (forward.sqrMagnitude < 0.001f)
                    forward = Vector3.forward;
                forward.Normalize();

                Vector3 viewPosition = playerCamera != null
                    ? view.position
                    : rig.transform.position + Vector3.up * 1.4f;
                Vector3 position = viewPosition + forward * 0.9f + Vector3.down * 0.2f;
                Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
                string barcode = crate.Barcode.ID;

                if (TrySpawnThroughFusionBridge(barcode, crate.name, position, rotation))
                    return true;

                Spawnable spawnable = new()
                {
                    crateRef = new SpawnableCrateReference(barcode)
                };
                AssetSpawner.Register(spawnable);

                var noVelocity = new Il2CppSystem.Nullable<Vector3>(Vector3.zero) { hasValue = false };
                var noSpawnId = new Il2CppSystem.Nullable<int>(0) { hasValue = false };
                var awaiter = AssetSpawner.SpawnAsync(
                    spawnable,
                    position,
                    rotation,
                    noVelocity,
                    null,
                    false,
                    noSpawnId,
                    null,
                    null,
                    null
                ).GetAwaiter();

                string itemName = crate.name;
                awaiter.OnCompleted((Il2CppSystem.Action)(Action)delegate
                {
                    try
                    {
                        Poolee spawned = awaiter.GetResult();
                        if (spawned == null)
                        {
                            MelonLogger.Warning($"[Void Engine] BONELAB's spawner returned no object for '{itemName}'.");
                            return;
                        }

                        MelonLogger.Msg($"[Void Engine] Spawned '{itemName}' in front of the player.");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning(
                            $"[Void Engine] Could not finish spawning '{itemName}': {ex.GetBaseException().Message}"
                        );
                    }
                });

                MelonLogger.Msg($"[Void Engine] Requested spawn for '{itemName}'.");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not spawn the selected item: {ex.GetBaseException().Message}"
                );
                return false;
            }
        }

        private static bool TrySpawnThroughFusionBridge(
            string barcode,
            string itemName,
            Vector3 position,
            Quaternion rotation
        )
        {
            bool fusionLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => string.Equals(assembly.GetName().Name, "LabFusion", StringComparison.Ordinal));
            if (!fusionLoaded)
                return false;

            try
            {
                Type? bridge = typeof(GiveItemCommand).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? spawn = bridge?.GetMethod("TrySpawn", BindingFlags.Public | BindingFlags.Static);
                if (spawn == null)
                {
                    MelonLogger.Warning(
                        "[Void Engine] Fusion is loaded, but the network spawn bridge is unavailable; item spawn was skipped."
                    );
                    return true;
                }

                return (bool)(spawn.Invoke(null, new object[] { barcode, itemName, position, rotation }) ?? false);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Fusion item spawn request failed: {ex.GetBaseException().Message}"
                );
                return true;
            }
        }
    }
}
