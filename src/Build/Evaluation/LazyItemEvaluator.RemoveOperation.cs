// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Construction;
using Microsoft.Build.Shared;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        class RemoveOperation : LazyItemOperation
        {
            readonly ImmutableList<string> _matchOnMetadata;
            private MetadataTrie<P, I> _metadataSet;

            public RemoveOperation(RemoveOperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
                : base(builder, lazyEvaluator)
            {
                _matchOnMetadata = builder.MatchOnMetadata.ToImmutable();

                ProjectFileErrorUtilities.VerifyThrowInvalidProjectFile(
                    _matchOnMetadata.IsEmpty || _itemSpec.Fragments.All(f => f is ItemSpec<ProjectProperty, ProjectItem>.ItemExpressionFragment),
                    new BuildEventFileInfo(string.Empty),
                    "OM_MatchOnMetadataIsRestrictedToReferencedItems");

                if (!_matchOnMetadata.IsEmpty)
                {
                    _metadataSet = new MetadataTrie<P, I>(builder.MatchOnMetadataOptions, _matchOnMetadata, _itemSpec);
                }
            }

            /// <summary>
            /// Apply the Remove operation.
            /// </summary>
            /// <remarks>
            /// This operation is mostly implemented in terms of the default <see cref="LazyItemOperation.ApplyImpl(OrderedItemDataCollection.Builder, ImmutableHashSet{string})"/>.
            /// This override exists to apply the removing-everything short-circuit.
            /// </remarks>
            protected override void ApplyImpl(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                if (_matchOnMetadata.IsEmpty && ItemspecContainsASingleBareItemReference(_itemSpec, _itemElement.ItemType) && _conditionResult)
                {
                    // Perf optimization: If the Remove operation references itself (e.g. <I Remove="@(I)"/>)
                    // then all items are removed and matching is not necessary
                    listBuilder.Clear();
                    return;
                }

                // todo Perf: do not match against the globs: https://github.com/Microsoft/msbuild/issues/2329
                HashSet<I> items = null;
                if (_matchOnMetadata.IsEmpty)
                {
                    foreach (I item in RemoveMatchingItemsFromDictionary(listBuilder.Dictionary))
                    {
                        items ??= new HashSet<I>();
                        items.Add(item);
                    }
                }
                else
                {
                    foreach (ItemData item in listBuilder)
                    {
                        if (MatchesItemOnMetadata(item.Item))
                        {
                            items ??= new HashSet<I>();
                            items.Add(item.Item);
                        }
                    }
                }

                if (items != null)
                {
                    listBuilder.RemoveAll(items, alreadyRemovedFromDictionary: _matchOnMetadata.IsEmpty);
                }
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="dictionary"></param>
            /// <returns></returns>
            private IEnumerable<I> RemoveMatchingItemsFromDictionary(IDictionary<string, OrderedItemDataCollection.DictionaryValue> dictionary)
            {
                foreach (var fragment in _itemSpec.Fragments)
                {
                    IEnumerable<string> referencedItems = fragment.GetReferencedItems();
                    if (referencedItems != null)
                    {
                        // The fragment can enumerate its referenced items, we can do dictionary lookups.
                        foreach (var spec in referencedItems)
                        {
                            string key = FileUtilities.NormalizePathForComparisonNoThrow(spec, fragment.ProjectDirectory);
                            if (dictionary.TryGetValue(key, out var innerList))
                            {
                                foreach (I item in innerList)
                                {
                                    yield return item;
                                }
                                dictionary.Remove(key);
                            }
                        }
                    }
                    else
                    {
                        // The fragment cannot enumerate its referenced items. Iterate over the dictionary and test each item.
                        List<string> keysToRemove = null;
                        foreach (var kvp in dictionary)
                        {
                            if (fragment.IsMatchNormalized(kvp.Key))
                            {
                                foreach (I item in kvp.Value)
                                {
                                    yield return item;
                                }
                                keysToRemove ??= new List<string>();
                                keysToRemove.Add(kvp.Key);
                            }
                        }

                        if (keysToRemove != null)
                        {
                            foreach (string key in keysToRemove)
                            {
                                dictionary.Remove(key);
                            }
                        }
                    }
                }
            }

            private bool MatchesItemOnMetadata(I item)
            {
                return _metadataSet.Contains(_matchOnMetadata.Select(m => item.GetMetadataValue(m)));
            }

            public ImmutableHashSet<string>.Builder GetRemovedGlobs()
            {
                var builder = ImmutableHashSet.CreateBuilder<string>();

                if (!_conditionResult)
                {
                    return builder;
                }

                var globs = _itemSpec.Fragments.OfType<GlobFragment>().Select(g => g.TextFragment);

                builder.UnionWith(globs);

                return builder;
            }
        }

        class RemoveOperationBuilder : OperationBuilder
        {
            public ImmutableList<string>.Builder MatchOnMetadata { get; } = ImmutableList.CreateBuilder<string>();

            public MatchOnMetadataOptions MatchOnMetadataOptions { get; set; }

            public RemoveOperationBuilder(ProjectItemElement itemElement, bool conditionResult) : base(itemElement, conditionResult)
            {
            }
        }
    }
}
