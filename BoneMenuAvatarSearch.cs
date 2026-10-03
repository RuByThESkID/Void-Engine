using System;
using System.Collections.Generic;
using System.Linq;
using BoneLib;
using BoneLib.BoneMenu;
using Il2CppSLZ.Marrow.Warehouse;
using MelonLoader;
using UnityEngine;
using Page = BoneLib.BoneMenu.Page;

namespace VoidEngine
{
    internal static class BoneMenuAvatarSearch
    {
        private const int ResultsPerPage = 4;
        private static readonly List<AvatarCrate> Catalog = new();
        private static List<AvatarCrate> _filtered = new();
        private static readonly List<Element> DynamicElements = new();
        private static Page? _searchPage;
        private static int _page;
        private static bool _catalogLoaded;

        internal static void Initialize(Page root)
        {
            _searchPage = root.CreatePage("Avatar Search", Main.VoidworksPurple);
            _searchPage.CreateString(
                "Search Avatars",
                Main.VoidworksPurple,
                string.Empty,
                Search
            );
            RenderResults(string.Empty);
        }

        private static void Search(string query)
        {
            _page = 0;
            if (!TryLoadCatalog())
            {
                RenderStatus("Avatar catalogue is not ready yet.");
                return;
            }

            string trimmedQuery = (query ?? string.Empty).Trim();
            _filtered = Catalog
                .Where(crate => Matches(crate, trimmedQuery))
                .OrderBy(crate => Relevance(crate, trimmedQuery))
                .ThenBy(crate => crate.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            RenderResults(trimmedQuery);
        }

        internal static bool TryApplyAvatarByQuery(string query)
        {
            string trimmedQuery = (query ?? string.Empty).Trim();
            if (trimmedQuery.Length == 0)
            {
                MelonLogger.Warning("[Void Engine] Avatar command needs a name, for example: function_avatar: nullbody");
                return false;
            }

            if (!TryLoadCatalog())
            {
                MelonLogger.Warning("[Void Engine] Avatar command could not run because the avatar catalogue is not ready.");
                return false;
            }

            string normalizedQuery = CatalogSearchText.Normalize(trimmedQuery);
            if (normalizedQuery.Length == 0)
            {
                MelonLogger.Warning("[Void Engine] Avatar name must contain letters or numbers.");
                return false;
            }

            AvatarCrate? exactMatch = Catalog
                .Where(crate => string.Equals(
                    CatalogSearchText.Normalize(crate.name),
                    normalizedQuery,
                    StringComparison.Ordinal
                ))
                .OrderBy(crate => crate.name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (exactMatch != null)
                return ApplyAvatar(exactMatch);

            List<string> suggestions = GetCommandSuggestions(trimmedQuery, normalizedQuery);
            if (suggestions.Count == 0)
            {
                MelonLogger.Warning(
                    $"[Void Engine] No exact avatar name matched '{trimmedQuery}', and no similar names were found."
                );
                return false;
            }

            MelonLogger.Warning(
                $"[Void Engine] '{trimmedQuery}' is not an exact avatar name. Copy a suggestion below and enter it as function_avatar: <name>."
            );
            for (int i = 0; i < suggestions.Count; i++)
                MelonLogger.Msg($"[Void Engine] Avatar suggestion {i + 1}: {suggestions[i]}");
            return false;
        }

        private static List<string> GetCommandSuggestions(string query, string normalizedQuery)
        {
            List<string> matchingNames = Catalog
                .Where(crate => Matches(crate, query))
                .OrderBy(crate => Relevance(crate, query))
                .ThenBy(crate => crate.name, StringComparer.OrdinalIgnoreCase)
                .Select(crate => crate.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            if (matchingNames.Count > 0)
                return matchingNames;

            int maxDistance = CatalogSearchText.SimilarityThreshold(normalizedQuery);
            return Catalog
                .Select(crate => crate.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new
                {
                    Name = name,
                    Distance = CatalogSearchText.LevenshteinDistance(
                        CatalogSearchText.Normalize(name),
                        normalizedQuery
                    )
                })
                .Where(candidate => candidate.Distance <= maxDistance)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(candidate => candidate.Name)
                .ToList();
        }

        private static bool TryLoadCatalog()
        {
            if (_catalogLoaded)
                return true;

            try
            {
                var crates = AssetWarehouse.Instance.GetCrates<AvatarCrate>(
                    (ICrateFilter<AvatarCrate>)null!
                );

                Catalog.Clear();
                HashSet<string> seenBarcodes = new(StringComparer.OrdinalIgnoreCase);
                foreach (AvatarCrate? crate in crates)
                {
                    if (crate == null || crate.Barcode == null || string.IsNullOrWhiteSpace(crate.Barcode.ID))
                        continue;

                    if (seenBarcodes.Add(crate.Barcode.ID))
                        Catalog.Add(crate);
                }

                _catalogLoaded = true;
                MelonLogger.Msg($"[Void Engine] Avatar Search indexed {Catalog.Count} avatars.");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not load the avatar catalogue: {ex.GetBaseException().Message}"
                );
                return false;
            }
        }

        private static bool Matches(AvatarCrate crate, string query)
        {
            if (query.Length == 0)
                return true;

            string normalizedQuery = CatalogSearchText.Normalize(query);
            if (normalizedQuery.Length == 0)
                return false;

            string barcode = crate.Barcode != null ? crate.Barcode.ID : string.Empty;
            string pallet = crate._pallet != null ? crate._pallet.name : string.Empty;
            string author = crate._pallet != null ? crate._pallet.Author : string.Empty;
            return CatalogSearchText.ContainsNormalized(crate.name, normalizedQuery) ||
                   CatalogSearchText.ContainsNormalized(barcode, normalizedQuery) ||
                   CatalogSearchText.ContainsNormalized(pallet, normalizedQuery) ||
                   CatalogSearchText.ContainsNormalized(author, normalizedQuery);
        }

        private static int Relevance(AvatarCrate crate, string query)
        {
            if (query.Length == 0)
                return 0;

            string name = CatalogSearchText.Normalize(crate.name);
            string normalizedQuery = CatalogSearchText.Normalize(query);
            if (string.Equals(name, normalizedQuery, StringComparison.Ordinal))
                return 0;
            if (name.StartsWith(normalizedQuery, StringComparison.Ordinal))
                return 1;
            if (name.IndexOf(normalizedQuery, StringComparison.Ordinal) >= 0)
                return 2;
            return 3;
        }

        private static void RenderStatus(string message)
        {
            if (_searchPage == null)
                return;

            ClearDynamicElements();
            AddDynamicElement(_searchPage.CreateFunction(message, Main.VoidworksPurple, null));
        }

        private static void RenderResults(string query)
        {
            if (_searchPage == null)
                return;

            ClearDynamicElements();

            if (string.IsNullOrWhiteSpace(query))
            {
                AddDynamicElement(_searchPage.CreateFunction(
                    "Enter a name, then press Enter to search",
                    Main.VoidworksPurple,
                    null
                ));
                return;
            }

            int pageCount = Math.Max(1, (_filtered.Count + ResultsPerPage - 1) / ResultsPerPage);
            _page = Math.Clamp(_page, 0, pageCount - 1);
            AddDynamicElement(_searchPage.CreateFunction(
                _filtered.Count == 0
                    ? $"No avatars found for '{query}'"
                    : $"{_filtered.Count} matches · page {_page + 1}/{pageCount}",
                Main.VoidworksPurple,
                null
            ));

            if (_page > 0)
            {
                AddDynamicElement(_searchPage.CreateFunction("Previous Results", Main.VoidworksPurple, () =>
                {
                    _page--;
                    RenderResults(query);
                }));
            }

            if (_page < pageCount - 1)
            {
                AddDynamicElement(_searchPage.CreateFunction("Next Results", Main.VoidworksPurple, () =>
                {
                    _page++;
                    RenderResults(query);
                }));
            }

            int start = _page * ResultsPerPage;
            int end = Math.Min(start + ResultsPerPage, _filtered.Count);
            for (int i = start; i < end; i++)
            {
                AvatarCrate crate = _filtered[i];
                string name = crate.name;
                AddDynamicElement(_searchPage.CreateFunction(name, Main.VoidworksPurple, () => ApplyAvatar(crate)));
            }
        }

        private static void ClearDynamicElements()
        {
            if (_searchPage == null)
                return;

            foreach (Element element in DynamicElements)
                _searchPage.Remove(element);
            DynamicElements.Clear();
        }

        private static void AddDynamicElement(Element element)
        {
            DynamicElements.Add(element);
        }

        private static bool ApplyAvatar(AvatarCrate crate)
        {
            try
            {
                if (crate == null || crate.Barcode == null || Player.RigManager == null)
                {
                    MelonLogger.Warning("[Void Engine] Could not apply the selected avatar: required references are missing.");
                    return false;
                }

                Player.RigManager.SwapAvatarCrate(new Barcode(crate.Barcode.ID), true, null);
                MelonLogger.Msg($"[Void Engine] Applying avatar: {crate.name}");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Avatar selection failed: {ex.GetBaseException().Message}"
                );
                return false;
            }
        }
    }
}
