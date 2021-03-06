﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.TableOfContents
{
    using System;
    using System.Collections.Generic;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.DataContracts.Common;
    using Microsoft.DocAsCode.Plugins;
    using Microsoft.DocAsCode.Utility;

    using TypeForwardedToRelativePath = Microsoft.DocAsCode.Common.RelativePath;

    internal sealed class TocResolver
    {
        private readonly IHostService _host;
        private readonly Dictionary<string, TocItemInfo> _collection;
        private readonly Dictionary<FileAndType, TocItemInfo> _notInProjectTocCache = new Dictionary<FileAndType, TocItemInfo>();

        public TocResolver(IHostService host, Dictionary<string, TocItemInfo> collection)
        {
            _host = host;
            _collection = collection;
        }

        public TocItemInfo Resolve(string file)
        {
            return ResolveItem(_collection[file], new Stack<FileAndType>());
        }

        private TocItemInfo ResolveItem(TocItemInfo wrapper, Stack<FileAndType> stack)
        {
            using (new LoggerFileScope(wrapper.File.File))
            {
                return ResolveItemCore(wrapper, stack);
            }
        }

        private TocItemInfo ResolveItemCore(TocItemInfo wrapper, Stack<FileAndType> stack)
        {
            if (wrapper.IsResolved)
            {
                return wrapper;
            }

            var file = wrapper.File;
            if (stack.Contains(file))
            {
                throw new DocumentException($"Circular reference to {file.FullPath} is found in {stack.Peek().FullPath}");
            }

            var item = wrapper.Content;

            // HomepageUid and Uid is deprecated, unified to TopicUid
            if (string.IsNullOrEmpty(item.TopicUid))
            {
                if (!string.IsNullOrEmpty(item.Uid))
                {
                    item.TopicUid = item.Uid;
                    item.Uid = null;
                }
                else if (!string.IsNullOrEmpty(item.HomepageUid))
                {
                    item.TopicUid = item.HomepageUid;
                    Logger.LogWarning($"HomepageUid is deprecated in TOC. Please use topicUid to specify uid {item.Homepage}");
                    item.HomepageUid = null;
                }
            }
            // Homepage is deprecated, unified to TopicHref
            if (!string.IsNullOrEmpty(item.Homepage))
            {
                if (string.IsNullOrEmpty(item.TopicHref))
                {
                    item.TopicHref = item.Homepage;
                }
                else
                {
                    Logger.LogWarning($"Homepage is deprecated in TOC. Homepage {item.Homepage} is overwritten with topicHref {item.TopicHref}");
                }
            }
            // TocHref supports 2 forms: absolute path and local toc file.
            // When TocHref is set, using TocHref as Href in output, and using Href as Homepage in output
            var tocHrefType = Utility.GetHrefType(item.TocHref);

            // check whether toc exists
            TocItemInfo tocFileModel = null;
            if (!string.IsNullOrEmpty(item.TocHref) && (tocHrefType == HrefType.MarkdownTocFile || tocHrefType == HrefType.YamlTocFile))
            {
                var tocFilePath = (TypeForwardedToRelativePath)file.File + (TypeForwardedToRelativePath)item.TocHref;
                var tocFile = file.ChangeFile(tocFilePath);
                if (!_collection.TryGetValue(tocFile.FullPath, out tocFileModel))
                {
                    var message = $"Unable to find {item.TocHref}. Make sure the file is included in config file docfx.json!";
                    Logger.LogWarning(message);
                }
            }

            if (!string.IsNullOrEmpty(item.TocHref))
            {
                if (!string.IsNullOrEmpty(item.Homepage))
                {
                    throw new DocumentException(
                        $"TopicHref should be used to specify the homepage for {item.TocHref} when tocHref is used.");
                }
                if (tocHrefType == HrefType.RelativeFile || tocHrefType == HrefType.RelativeFolder)
                {
                    throw new DocumentException($"TocHref {item.TocHref} only supports absolute path or local toc file.");
                }
            }

            var hrefType = Utility.GetHrefType(item.Href);
            switch (hrefType)
            {
                case HrefType.AbsolutePath:
                case HrefType.RelativeFile:
                    if (item.Items != null && item.Items.Count > 0)
                    {
                        for (int i = 0; i < item.Items.Count; i++)
                        {
                            item.Items[i] = ResolveItem(new TocItemInfo(file, item.Items[i]), stack).Content;
                        }
                        if (string.IsNullOrEmpty(item.TopicHref) && string.IsNullOrEmpty(item.TopicUid))
                        {
                            var defaultItem = GetDefaultHomepageItem(item);
                            if (defaultItem != null)
                            {
                                item.AggregatedHref = defaultItem.TopicHref;
                                item.AggregatedUid = defaultItem.TopicUid;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(item.TopicHref))
                    {
                        // Get homepage from TocHref if href/topicHref is null or empty
                        if (string.IsNullOrEmpty(item.Href) && string.IsNullOrEmpty(item.TopicUid) && tocFileModel != null)
                        {
                            stack.Push(file);
                            var resolved = ResolveItem(tocFileModel, stack).Content;
                            stack.Pop();
                            item.Href = resolved.TopicHref ?? resolved.AggregatedHref;
                            item.TopicUid = resolved.TopicUid ?? resolved.AggregatedUid;
                        }
                        // Use TopicHref in output model
                        item.TopicHref = item.Href;
                    }
                    break;
                case HrefType.RelativeFolder:
                    {
                        if (tocFileModel != null)
                        {
                            Logger.LogWarning($"Href {item.Href} is overwritten by tocHref {item.TocHref}");
                        }
                        else
                        {
                            var relativeFolder = (TypeForwardedToRelativePath)file.File + (TypeForwardedToRelativePath)item.Href;
                            var tocFilePath = relativeFolder + (TypeForwardedToRelativePath)Constants.YamlTocFileName;

                            var tocFile = file.ChangeFile(tocFilePath);

                            // First, try finding toc.yml under the relative folder
                            // Second, try finding toc.md under the relative folder
                            if (!_collection.TryGetValue(tocFile.FullPath, out tocFileModel))
                            {
                                tocFilePath = relativeFolder + (TypeForwardedToRelativePath)Constants.MarkdownTocFileName;
                                tocFile = file.ChangeFile(tocFilePath);
                                if (!_collection.TryGetValue(tocFile.FullPath, out tocFileModel))
                                {
                                    var message =
                                        $"Unable to find either {Constants.YamlTocFileName} or {Constants.MarkdownTocFileName} inside {item.Href}. Make sure the file is included in config file docfx.json!";
                                    Logger.LogWarning(message);
                                    break;
                                }
                            }

                            item.TocHref = tocFilePath - (TypeForwardedToRelativePath)file.File;
                        }

                        // Get homepage from TocHref if TopicHref/TopicUid is not specified
                        if (string.IsNullOrEmpty(item.TopicHref) && string.IsNullOrEmpty(item.TopicUid))
                        {
                            stack.Push(file);
                            var resolved = ResolveItem(tocFileModel, stack).Content;
                            stack.Pop();
                            item.Href = item.TopicHref = resolved.TopicHref ?? resolved.AggregatedHref;
                            item.TopicUid = resolved.TopicUid ?? resolved.AggregatedUid;
                        }
                        else
                        {
                            item.Href = item.TopicHref;
                        }

                        if (item.Items != null)
                        {
                            for (int i = 0; i < item.Items.Count; i++)
                            {
                                item.Items[i] = ResolveItem(new TocItemInfo(file, item.Items[i]), stack).Content;
                            }
                        }
                    }
                    break;
                case HrefType.MarkdownTocFile:
                case HrefType.YamlTocFile:
                    {
                        var tocFilePath = (TypeForwardedToRelativePath)file.File + (TypeForwardedToRelativePath)item.Href;
                        var tocFile = file.ChangeFile(tocFilePath);
                        TocItemInfo referencedTocFileModel;
                        TocItemViewModel referencedToc;
                        stack.Push(file);
                        if (_collection.TryGetValue(tocFile.FullPath, out referencedTocFileModel) || _notInProjectTocCache.TryGetValue(tocFile, out referencedTocFileModel))
                        {
                            referencedTocFileModel = ResolveItem(referencedTocFileModel, stack);
                            referencedTocFileModel.IsReferenceToc = true;
                            referencedToc = referencedTocFileModel.Content;
                        }
                        else
                        {
                            // It is acceptable that the referenced toc file is not included in docfx.json, as long as it can be found locally
                            referencedTocFileModel = new TocItemInfo(tocFile, new TocItemViewModel
                            {
                                Items = Utility.LoadSingleToc(tocFile.FullPath)
                            });

                            referencedTocFileModel = ResolveItem(referencedTocFileModel, stack);
                            referencedToc = referencedTocFileModel.Content;
                            _notInProjectTocCache[tocFile] = referencedTocFileModel;
                        }
                        stack.Pop();
                        // For referenced toc, content from referenced toc is expanded as the items of current toc item,
                        // Href is reset to the homepage of current toc item
                        item.Href = item.TopicHref;
                        item.Items = referencedToc.Items;
                    }
                    break;
                default:
                    break;
            }

            var relativeToFile = (TypeForwardedToRelativePath)file.File;

            item.OriginalHref = item.Href;
            item.OriginalTocHref = item.TocHref;
            item.OriginalTopicHref = item.TopicHref;
            item.OriginalHomepage = item.Homepage;
            item.Href = NormalizeHref(item.Href, relativeToFile);
            item.TocHref = NormalizeHref(item.TocHref, relativeToFile);
            item.TopicHref = NormalizeHref(item.TopicHref, relativeToFile);
            item.Homepage = NormalizeHref(item.Homepage, relativeToFile);

            wrapper.IsResolved = true;

            // for backward compatibility
            if (item.Href == null && item.Homepage == null)
            {
                item.Href = item.TocHref;
                item.Homepage = item.TopicHref;
            }

            return wrapper;
        }

        private string NormalizeHref(string href, TypeForwardedToRelativePath relativeToFile)
        {
            if (!Utility.IsSupportedRelativeHref(href))
            {
                return href;
            }

            return (relativeToFile + (TypeForwardedToRelativePath)href).GetPathFromWorkingFolder();
        }

        private TocItemViewModel GetDefaultHomepageItem(TocItemViewModel toc)
        {
            if (toc == null || toc.Items == null)
            {
                return null;
            }

            foreach (var item in toc.Items)
            {
                var tocItem = TreeIterator.PreorderFirstOrDefault(item, s => s.Items, s => IsValidHomepageLink(s));
                if (tocItem != null)
                {
                    return tocItem;
                }
            }
            return null;
        }

        /// <summary>
        /// Valid homepage href should:
        /// 1. relative file path
        /// 2. refer to a file
        /// 3. folder is not supported
        /// 4. refer to an `uid`
        /// </summary>
        /// <param name="href"></param>
        /// <returns></returns>
        private bool IsValidHomepageLink(TocItemViewModel tocItem)
        {
            if (!string.IsNullOrEmpty(tocItem.TopicUid))
            {
                return true;
            }

            var hrefType = Utility.GetHrefType(tocItem.Href);
            if (hrefType == HrefType.RelativeFile)
            {
                return true;
            }

            return false;
        }
    }
}
