﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;
using EditorCompletion = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion.EditorImplementation
{
    internal class EditorAsyncCompletionService : IAsyncCompletionItemManager
    {
        private readonly IAsyncCompletionBroker _broker;
        private readonly CompletionHelper _completionHelper;

        private const int MaxMRUSize = 10;
        private ImmutableArray<string> _recentItems = ImmutableArray<string>.Empty;

        public EditorAsyncCompletionService(IAsyncCompletionBroker broker)
        {
            _broker = broker;
            _completionHelper = new CompletionHelper(isCaseSensitive: true);
        }

        public Task<ImmutableArray<EditorCompletion.CompletionItem>> SortCompletionListAsync(
            IAsyncCompletionSession session,
            EditorCompletion.AsyncCompletionSessionInitialDataSnapshot data,
            CancellationToken token)
        {
            session.ItemCommitted += ItemCommitted;
            session.Dismissed += SessionDismissed;
            return Task.FromResult(data.InitialList.OrderBy(i => i.SortText).ToImmutableArray());
        }

        private void ItemCommitted(object sender, EditorCompletion.CompletionItemEventArgs e)
        {
            MakeMostRecentItem(e.Item.DisplayText);
        }

        private void SessionDismissed(object sender, EventArgs e)
        {
            // TODO: Unhook the session's events when the session is available in the args
        }

        public async Task<EditorCompletion.FilteredCompletionModel> UpdateCompletionListAsync(
            IAsyncCompletionSession session, 
            EditorCompletion.AsyncCompletionSessionDataSnapshot data, 
            CancellationToken cancellationToken)
        {
            var mustSetSelection = false;

            if (data.IsUnavailable && 
                (data.UpdateTrigger.Reason == EditorCompletion.UpdateTriggerReason.Insertion || data.UpdateTrigger.Reason == EditorCompletion.UpdateTriggerReason.Deletion))
            {
                var document = data.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document == null)
                {
                    // Uhh...
                    return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0);
                }

                var completionService = document.GetLanguageService<CompletionService>();
                var completionList = await completionService.GetCompletionsAsync(
                    document,
                    session.ApplicableToSpan.GetEndPoint(data.Snapshot),
                    GetRoslynTrigger(data.UpdateTrigger)).ConfigureAwait(false);

                if (completionList != null && completionList.Items.Length > 0)
                {
                    // Okay, we want completion. How do I communicate that?
                    mustSetSelection = true;
                }
                else
                {
                    // Still don't want completion. 
                    return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0);
                }
            }

            var filterText = session.ApplicableToSpan.GetText(data.Snapshot);

            // Check if the user is typing a number. If so, only proceed if it's a number
            // directly after a <dot>. That's because it is actually reasonable for completion
            // to be brought up after a <dot> and for the user to want to filter completion
            // items based on a number that exists in the name of the item. However, when
            // we are not after a dot (i.e. we're being brought up after <space> is typed)
            // then we don't want to filter things. Consider the user writing:
            //
            //      dim i =<space>
            //
            // We'll bring up the completion list here (as VB has completion on <space>).
            // If the user then types '3', we don't want to match against Int32.
            if (filterText.Length > 0 && char.IsNumber(filterText[0]))
            {
                if (!IsAfterDot(data.Snapshot, session.ApplicableToSpan))
                {
                    return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0);
                }
            }

            // We need to filter if a non-empty strict subset of filters are selected
            var selectedFilters = data.SelectedFilters.Where(f => f.IsSelected).Select(f => f.Filter).ToImmutableArray();
            var needToFilter = selectedFilters.Length > 0 && selectedFilters.Length < data.SelectedFilters.Length;

            var initialListOfItemsToBeIncluded = new List<FilterResult>();
            foreach (var item in data.InitialSortedList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (needToFilter && ShouldBeFilteredOutOfCompletionList(item, selectedFilters))
                {
                    continue;
                }

                if (MatchesFilterText(item, filterText, data.InitialTrigger.Reason, data.UpdateTrigger.Reason))
                {
                    initialListOfItemsToBeIncluded.Add(new FilterResult(item, filterText, matchedFilterText: true));
                }
                else
                {
                    if (data.InitialTrigger.Reason == EditorCompletion.InitialTriggerReason.Deletion ||
                        data.InitialTrigger.Reason == EditorCompletion.InitialTriggerReason.Invoke ||
                        filterText.Length <= 1)
                    {
                        initialListOfItemsToBeIncluded.Add(new FilterResult(item, filterText, matchedFilterText: false));
                    }
                }
            }

            if (initialListOfItemsToBeIncluded.Count == 0)
            {
                return HandleAllItemsFilteredOut(data.InitialTrigger.Reason, data.SelectedFilters, selectedFilters, mustSetSelection);
            }

            // If this was deletion, then we control the entire behavior of deletion
            // ourselves.
            if (data.InitialTrigger.Reason == EditorCompletion.InitialTriggerReason.Deletion)
            {
                return HandleDeletionTrigger(data.InitialSortedList, data.InitialTrigger.Reason, data.SelectedFilters, data.UpdateTrigger.Reason, filterText, initialListOfItemsToBeIncluded, mustSetSelection);
            }

            var caretPoint = session.TextView.GetCaretPoint(data.Snapshot.TextBuffer);
            var caretPosition = caretPoint.HasValue ? caretPoint.Value.Position : (int?)null;

            var snapshotForDocument = data.InitialSortedList.FirstOrDefault(i => i.Properties.ContainsProperty(CompletionItemSource.TriggerBuffer))?.Properties.GetProperty<ITextBuffer>(CompletionItemSource.TriggerBuffer).CurrentSnapshot ?? data.Snapshot;

            return HandleNormalFiltering(
                data.InitialSortedList,
                snapshotForDocument,
                caretPosition,
                filterText,
                data.SelectedFilters,
                data.UpdateTrigger.Reason,
                initialListOfItemsToBeIncluded,
                data.InitialTrigger.Reason,
                mustSetSelection);
        }

        private static RoslynTrigger GetRoslynTrigger(EditorCompletion.UpdateTrigger trigger)
        {
            RoslynTrigger roslynTrigger = default;
            switch (trigger.Reason)
            {
                case EditorCompletion.UpdateTriggerReason.Insertion:
                    roslynTrigger = RoslynTrigger.CreateInsertionTrigger(trigger.Character);
                    break;
                case EditorCompletion.UpdateTriggerReason.Deletion:
                    roslynTrigger = RoslynTrigger.CreateDeletionTrigger(trigger.Character);
                    break;
            }

            return roslynTrigger;
        }

        private EditorCompletion.FilteredCompletionModel HandleDeletionTrigger(
            ImmutableArray<EditorCompletion.CompletionItem> sortedList,
            EditorCompletion.InitialTriggerReason triggerReason,
            ImmutableArray<EditorCompletion.CompletionFilterWithState> filters,
            EditorCompletion.UpdateTriggerReason filterReason,
            string filterText,
            List<FilterResult> filterResults,
            bool mustSetSelection)
        {
            if (filterReason == EditorCompletion.UpdateTriggerReason.Insertion && !filterResults.Any(r => r.MatchedFilterText))
            {
                // The user has typed something, but nothing in the actual list matched what
                // they were typing.  In this case, we want to dismiss completion entirely.
                // The thought process is as follows: we aggressively brough up completion
                // to help them when they typed delete (in case they wanted to pick another
                // item).  However, they're typing something that doesn't seem to match at all
                // The completion list is just distracting at this point.
                return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0, filters, mustSetSelection ? EditorCompletion.UpdateSelectionHint.SoftSelected : EditorCompletion.UpdateSelectionHint.NoChange,
                    centerSelection: true, uniqueItem: default);
            }

            FilterResult? bestFilterResult = null;
            int matchCount = 0;
            foreach (var currentFilterResult in filterResults.Where(r => r.MatchedFilterText))
            {
                if (bestFilterResult == null ||
                    IsBetterDeletionMatch(currentFilterResult, bestFilterResult.Value))
                {
                    // We had no best result yet, so this is now our best result.
                    bestFilterResult = currentFilterResult;
                    matchCount++;
                }
            }

            // If we had a matching item, then pick the best of the matching items and
            // choose that one to be hard selected.  If we had no actual matching items
            // (which can happen if the user deletes down to a single character and we
            // include everything), then we just soft select the first item.

            var filteredItems = filterResults.Select(r => r.CompletionItem).AsImmutable();
            var highlightedList = GetHighlightedList(filterResults, filterText).ToImmutableArray();
            var updatedFilters = GetUpdatedFilters(sortedList, filterResults, filters, filterText);

            if (bestFilterResult != null)
            {
                // Only hard select this result if it's a prefix match
                // We need to do this so that 
                // * deleting and retyping a dot in a member access does not change the 
                //   text that originally appeared before the dot
                // * deleting through a word from the end keeps that word selected
                // This also preserves the behavior the VB had through Dev12.
                var hardSelect = bestFilterResult.Value.CompletionItem.FilterText.StartsWith(filterText, StringComparison.CurrentCultureIgnoreCase);

                return new EditorCompletion.FilteredCompletionModel(highlightedList, filteredItems.IndexOf(bestFilterResult.Value.CompletionItem), updatedFilters, matchCount == 1 ? EditorCompletion.UpdateSelectionHint.Selected : EditorCompletion.UpdateSelectionHint.SoftSelected, centerSelection: true, uniqueItem: null);
            }
            else
            {
                return new EditorCompletion.FilteredCompletionModel(highlightedList, 0, updatedFilters, EditorCompletion.UpdateSelectionHint.SoftSelected, centerSelection: true, uniqueItem: null);
            }
        }

        private bool IsBetterDeletionMatch(FilterResult result1, FilterResult result2)
        {
            var item1 = result1.CompletionItem;
            var item2 = result2.CompletionItem;

            var prefixLength1 = item1.FilterText.GetCaseInsensitivePrefixLength(result1.FilterText);
            var prefixLength2 = item2.FilterText.GetCaseInsensitivePrefixLength(result2.FilterText);

            // Prefer the item that matches a longer prefix of the filter text.
            if (prefixLength1 > prefixLength2)
            {
                return true;
            }

            if (prefixLength1 == prefixLength2)
            {
                // If the lengths are the same, prefer the one with the higher match priority.
                // But only if it's an item that would have been hard selected.  We don't want
                // to aggressively select an item that was only going to be softly offered.

                var item1Priority = CalculatePriority(item1);
                var item2Priority = CalculatePriority(item2);

                if (item1Priority > item2Priority)
                {
                    return true;
                }
            }

            return false;
        }

        private int CalculatePriority(EditorCompletion.CompletionItem item)
        {
            if (!item.Properties.TryGetProperty<CompletionItemSelectionBehavior>("SelectionBehavior", out var itemSelectionBehavior) ||
                itemSelectionBehavior != CompletionItemSelectionBehavior.HardSelection)
            {
                return MatchPriority.Default;
            }
            else
            {
                return GetMatchPriority(item);
            }
        }

        private EditorCompletion.FilteredCompletionModel HandleNormalFiltering(
            ImmutableArray<EditorCompletion.CompletionItem> sortedList,
            ITextSnapshot snapshot,
            int? caretPosition,
            string filterText,
            ImmutableArray<EditorCompletion.CompletionFilterWithState> filters,
            EditorCompletion.UpdateTriggerReason filterReason,
            List<FilterResult> itemsInList,
            EditorCompletion.InitialTriggerReason triggerReason,
            bool mustSetSelection)
        {
            var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                var listWithSelections = GetHighlightedList(itemsInList, filterText);
                return new EditorCompletion.FilteredCompletionModel(listWithSelections.ToImmutableArray(), 0, filters, mustSetSelection ? EditorCompletion.UpdateSelectionHint.SoftSelected : EditorCompletion.UpdateSelectionHint.NoChange,
                    centerSelection: true, uniqueItem: default);
            }

            var completionService = document.GetLanguageService<CompletionService>();
            if (completionService == null)
            {
                var listWithSelections = GetHighlightedList(itemsInList, filterText);
                return new EditorCompletion.FilteredCompletionModel(listWithSelections.ToImmutableArray(), 0, filters, mustSetSelection ? EditorCompletion.UpdateSelectionHint.SoftSelected : EditorCompletion.UpdateSelectionHint.NoChange,
                    centerSelection: true, uniqueItem: default);
            }

            var matchingItems = itemsInList
                .Where(r => r.MatchedFilterText)
                .Select(t => GetOrCreateRoslynItem(t.CompletionItem))
                .AsImmutable();

            var chosenItems = completionService.FilterItems(snapshot.GetOpenDocumentInCurrentContextWithChanges(), matchingItems, filterText);

            var recentItems = _recentItems;
            var bestItem = GetBestItemBasedOnMRU(chosenItems, recentItems);
            var highlightedList = GetHighlightedList(itemsInList, filterText).ToImmutableArray();
            var updatedFilters = GetUpdatedFilters(sortedList, itemsInList, filters, filterText);

            // TODO: Can we get away with less complexity here by only doing hard select on preselection and not on regular filter text matching / etc...
            var isHardSelection = IsHardSelection(bestItem, snapshot, caretPosition, filterReason, filterText, triggerReason);

            if (bestItem == null)
            {
                return new EditorCompletion.FilteredCompletionModel(highlightedList, 0, updatedFilters, isHardSelection ? EditorCompletion.UpdateSelectionHint.Selected : EditorCompletion.UpdateSelectionHint.SoftSelected, centerSelection: true, uniqueItem: null);
            }

            // TODO: Better conversion between Roslyn/Editor completion items
            var selectedItemIndex = itemsInList.IndexOf(i => i.CompletionItem.DisplayText == bestItem.DisplayText);

            EditorCompletion.CompletionItem uniqueItem = null;
            if (bestItem != null && matchingItems.Length == 1 && filterText.Length > 0)
            {
                uniqueItem = highlightedList[selectedItemIndex].CompletionItem;
            }

            return new EditorCompletion.FilteredCompletionModel(highlightedList, selectedItemIndex, updatedFilters, isHardSelection ? EditorCompletion.UpdateSelectionHint.Selected : EditorCompletion.UpdateSelectionHint.SoftSelected, centerSelection: true, uniqueItem);
        }

        private RoslynCompletionItem GetOrCreateRoslynItem(EditorCompletion.CompletionItem item)
        {
            if (item.Properties.TryGetProperty<RoslynCompletionItem>("RoslynItem", out var roslynItem))
            {
                return roslynItem;
            }

            return RoslynCompletionItem.Create(item.DisplayText, item.FilterText, item.SortText);
        }

        private bool IsHardSelection(RoslynCompletionItem bestItem, ITextSnapshot snapshot, int? caretPosition, EditorCompletion.UpdateTriggerReason filterReason, string filterText, EditorCompletion.InitialTriggerReason triggerReason)
        {
            if (bestItem == null) // || model.UseSuggestionMode (there is a builder);
            {
                return false;
            }

            // We don't have a builder and we have a best match.  Normally this will be hard
            // selected, except for a few cases.  Specifically, if no filter text has been
            // provided, and this is not a preselect match then we will soft select it.  This
            // happens when the completion list comes up implicitly and there is something in
            // the MRU list.  In this case we do want to select it, but not with a hard
            // selection.  Otherwise you can end up with the following problem:
            //
            //  dim i as integer =<space>
            //
            // Completion will comes up after = with 'integer' selected (Because of MRU).  We do
            // not want 'space' to commit this.

            if (ShouldSoftSelectItem(bestItem, filterText, triggerReason))
            {
                return false;
            }

            // If the user moved the caret left after they started typing, the 'best' match may not match at all
            // against the full text span that this item would be replacing.
            if (!MatchesFilterText(bestItem, filterText, triggerReason, filterReason))
            {
                return false;
            }

            // TODO: Are there more cases?

            // There was either filter text, or this was a preselect match. In either case, we can
            // hard select this.
            return true;
        }

        private bool ShouldSoftSelectItem(RoslynCompletionItem item, string filterText, EditorCompletion.InitialTriggerReason triggerReason)
        {
            // If all that has been typed is puntuation, then don't hard select anything.
            // It's possible the user is just typing language punctuation and selecting
            // anything in the list will interfere.  We only allow this if the filter text
            // exactly matches something in the list already. 
            if (filterText.Length > 0 && IsAllPunctuation(filterText) && filterText != item.DisplayText)
            {
                return true;
            }

            // If the user hasn't actually typed anything, then don't hard select any item.
            // The only exception to this is if the completion provider has requested the
            // item be preselected.
            if (filterText.Length == 0)
            {
                // Item didn't want to be hard selected with no filter text.
                // So definitely soft select it.
                if (item.Rules.SelectionBehavior != CompletionItemSelectionBehavior.HardSelection)
                {
                    return true;
                }

                // Item did not ask to be preselected.  So definitely soft select it.
                if (item.Rules.MatchPriority == MatchPriority.Default)
                {
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<EditorCompletion.CompletionItemWithHighlight> GetHighlightedList(List<FilterResult> filterResults, string filterText)
        {
            var highlightedList = new List<EditorCompletion.CompletionItemWithHighlight>();
            foreach (var item in filterResults)
            {
                var highlightedSpans = _completionHelper.GetHighlightedSpans(item.CompletionItem.FilterText, filterText, CultureInfo.CurrentCulture);
                highlightedList.Add(new EditorCompletion.CompletionItemWithHighlight(item.CompletionItem, highlightedSpans.Select(s => s.ToSpan()).ToImmutableArray()));
            }

            return highlightedList;
        }

        private ImmutableArray<EditorCompletion.CompletionFilterWithState> GetUpdatedFilters(
            ImmutableArray<EditorCompletion.CompletionItem> originalList,
            List<FilterResult> filteredList,
            ImmutableArray<EditorCompletion.CompletionFilterWithState> filters,
            string filterText)
        {
            // See which filters might be enabled based on the typed code
            var textFilteredFilters = filteredList.SelectMany(n => n.CompletionItem.Filters).Distinct();

            // When no items are available for a given filter, it becomes unavailable
            return ImmutableArray.CreateRange(filters.Select(n => n.WithAvailability(textFilteredFilters.Contains(n.Filter))));
        }


        private static bool IsAllPunctuation(string filterText)
        {
            foreach (var ch in filterText)
            {
                if (!char.IsPunctuation(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAfterDot(ITextSnapshot snapshot, ITrackingSpan applicableToSpan)
        {
            var position = applicableToSpan.GetStartPoint(snapshot).Position;
            return position > 0 && snapshot.GetText(position - 1, 1) == ".";
        }

        private void MakeMostRecentItem(string item)
        {
            var updated = false;

            while (!updated)
            {
                var oldItems = _recentItems;
                var newItems = oldItems.Remove(item);

                if (newItems.Length == MaxMRUSize)
                {
                    // Remove the least recent item.
                    newItems = newItems.RemoveAt(0);
                }

                newItems = newItems.Add(item);
                updated = ImmutableInterlocked.InterlockedCompareExchange(ref _recentItems, newItems, oldItems) == oldItems;
            }
        }

        private RoslynCompletionItem GetBestItemBasedOnMRU(ImmutableArray<RoslynCompletionItem> chosenItems, ImmutableArray<string> recentItems)
        {
            if (chosenItems.Length == 0)
            {
                return null;
            }

            // Try to find the chosen item has been most recently used.
            var bestItem = chosenItems.First();

            for (int i = 0, n = chosenItems.Length; i < n; i++)
            {
                var chosenItem = chosenItems[i];
                var mruIndex1 = GetRecentItemIndex(recentItems, bestItem.DisplayText);
                var mruIndex2 = GetRecentItemIndex(recentItems, chosenItem.DisplayText);

                if (mruIndex2 < mruIndex1)
                {
                    bestItem = chosenItem;
                }
            }

            // If our best item appeared in the MRU list, use it
            if (GetRecentItemIndex(recentItems, bestItem.DisplayText) <= 0)
            {
                return bestItem;
            }

            // Otherwise use the chosen item that has the highest matchPriority.
            for (int i = 1, n = chosenItems.Length; i < n; i++)
            {
                var chosenItem = chosenItems[i];

                var bestItemPriority = GetMatchPriority(bestItem);
                var currentItemPriority = GetMatchPriority(chosenItem);

                if (currentItemPriority > bestItemPriority)
                {
                    bestItem = chosenItem;
                }
            }

            return bestItem;
        }

        private static int GetRecentItemIndex(ImmutableArray<string> recentItems, string itemDisplayText)
        {
            var index = recentItems.IndexOf(itemDisplayText);
            return -index;
        }

        private EditorCompletion.FilteredCompletionModel HandleAllItemsFilteredOut
            (EditorCompletion.InitialTriggerReason triggerReason,
            ImmutableArray<EditorCompletion.CompletionFilterWithState> filters,
            ImmutableArray<EditorCompletion.CompletionFilter> activeFilters,
            bool mustSetSelection)
        {
            // TODO: DismissIfEmpty?
            // If the user was just typing, and the list went to empty *and* this is a 
            // language that wants to dismiss on empty, then just return a null model
            // to stop the completion session.

            if (triggerReason == EditorCompletion.InitialTriggerReason.Insertion)
            {
                // TODO: Stop completion when that API is available
                return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0, filters, mustSetSelection ? EditorCompletion.UpdateSelectionHint.SoftSelected : EditorCompletion.UpdateSelectionHint.NoChange, centerSelection: true, uniqueItem: default);
            }

            if (activeFilters.Length > 0)
            {
                // If the user has turned on some filtering states, and we filtered down to 
                // nothing, then we do want the UI to show that to them.  That way the user
                // can turn off filters they don't want and get the right set of items.

                return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0, filters, mustSetSelection ? EditorCompletion.UpdateSelectionHint.SoftSelected : EditorCompletion.UpdateSelectionHint.NoChange, centerSelection: true, uniqueItem: default);
            }
            else
            {
                // If we are going to filter everything out, then just preserve the existing
                // model (and all the previously filtered items), but switch over to soft 
                // selection.

                return new EditorCompletion.FilteredCompletionModel(ImmutableArray<EditorCompletion.CompletionItemWithHighlight>.Empty, 0, filters, EditorCompletion.UpdateSelectionHint.SoftSelected, centerSelection: true, uniqueItem: null);
            }
        }

        private bool MatchesFilterText(
            RoslynCompletionItem item, 
            string filterText,
            EditorCompletion.InitialTriggerReason triggerReason, 
            EditorCompletion.UpdateTriggerReason filterReason)
        {
            return MatchesFilterText(
                item.FilterText,
                item.DisplayText,
                item.Rules.MatchPriority,
                filterText,
                triggerReason,
                filterReason);
        }

        private bool MatchesFilterText(
            EditorCompletion.CompletionItem item,
            string filterText,
            EditorCompletion.InitialTriggerReason triggerReason,
            EditorCompletion.UpdateTriggerReason filterReason)
        {
            return MatchesFilterText(
                item.FilterText,
                item.DisplayText,
                GetMatchPriority(item),
                filterText,
                triggerReason,
                filterReason);
        }

        private bool MatchesFilterText(
            string itemFilterText,
            string itemDisplayText,
            int matchPriority,
            string filterText,
            EditorCompletion.InitialTriggerReason triggerReason,
            EditorCompletion.UpdateTriggerReason filterReason)
        {
            // For the deletion we bake in the core logic for how matching should work.
            // This way deletion feels the same across all languages that opt into deletion 
            // as a completion trigger.

            // Specifically, to avoid being too aggressive when matching an item during 
            // completion, we require that the current filter text be a prefix of the 
            // item in the list.

            if (triggerReason == EditorCompletion.InitialTriggerReason.Deletion && filterReason == EditorCompletion.UpdateTriggerReason.Deletion)
            {
                return itemFilterText.GetCaseInsensitivePrefixLength(filterText) > 0;
            }

            // If the user hasn't typed anything, and this item was preselected, or was in the
            // MRU list, then we definitely want to include it.
            if (filterText.Length == 0)
            {
                // TODO: Need ItemRules.MatchPriority.
                if (matchPriority > MatchPriority.Default)
                {
                    return true;
                }

                if (!_recentItems.IsDefault && GetRecentItemIndex(_recentItems, itemDisplayText) <= 0)
                {
                    return true;
                }
            }

            return _completionHelper.MatchesPattern(itemFilterText, filterText, CultureInfo.CurrentCulture);
        }

        private bool ShouldBeFilteredOutOfCompletionList(EditorCompletion.CompletionItem item, ImmutableArray<EditorCompletion.CompletionFilter> activeFilters)
        {
            foreach (var itemFilter in item.Filters)
            {
                if (activeFilters.Contains(itemFilter))
                {
                    return false;
                }
            }

            return true;
        }

        private int GetMatchPriority(EditorCompletion.CompletionItem item)
        {
            return item.Properties.TryGetProperty<int>("MatchPriority", out var matchPriority)
                ? matchPriority
                : MatchPriority.Default;
        }

        private int GetMatchPriority(RoslynCompletionItem bestItem)
        {
            return bestItem.Rules.MatchPriority;
        }

        private struct FilterResult
        {
            public EditorCompletion.CompletionItem CompletionItem;
            public string FilterText;
            public bool MatchedFilterText;

            public FilterResult(EditorCompletion.CompletionItem item, string filterText, bool matchedFilterText)
            {
                CompletionItem = item;
                FilterText = filterText;
                MatchedFilterText = matchedFilterText;
            }
        }
    }
}