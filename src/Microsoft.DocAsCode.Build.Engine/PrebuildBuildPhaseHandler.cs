﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.Plugins;

    internal class PrebuildBuildPhaseHandler : IPhaseHandler
    {
        private DocumentBuildContext _context;

        public PrebuildBuildPhaseHandler(DocumentBuildContext context)
        {
            _context = context;
        }

        public void Handle(List<HostService> hostServices, int maxParallelism)
        {
            foreach (var hostService in hostServices)
            {
                using (new LoggerPhaseScope(hostService.Processor.Name, true))
                {
                    var steps = string.Join("=>", hostService.Processor.BuildSteps.OrderBy(step => step.BuildOrder).Select(s => s.Name));
                    Logger.LogInfo($"Building {hostService.Models.Count} file(s) in {hostService.Processor.Name}({steps})...");
                    Logger.LogVerbose($"Processor {hostService.Processor.Name}: Prebuilding...");
                    using (new LoggerPhaseScope("Prebuild", true))
                    {
                        Prebuild(hostService);
                    }
                    Logger.LogVerbose($"Processor {hostService.Processor.Name}: Building...");
                    using (new LoggerPhaseScope("Build", true))
                    {
                        BuildArticle(hostService, maxParallelism);
                    }
                }
            }
        }

        public virtual void PreHandle(List<HostService> hostServices)
        {
            if (_context == null)
            {
                return;
            }
            foreach (var hostService in hostServices)
            {
                hostService.SourceFiles = _context.AllSourceFiles;
                foreach (var m in hostService.Models)
                {
                    if (m.LocalPathFromRepoRoot == null)
                    {
                        m.LocalPathFromRepoRoot = StringExtension.ToDisplayPath(Path.Combine(m.BaseDir, m.File));
                    }
                    if (m.LocalPathFromRoot == null)
                    {
                        m.LocalPathFromRoot = StringExtension.ToDisplayPath(Path.Combine(m.BaseDir, m.File));
                    }
                }
            }
        }

        public virtual void PostHandle(List<HostService> hostServices)
        {
        }

        #region Private Methods

        private static void Prebuild(HostService hostService)
        {
            BuildPhaseUtility.RunBuildSteps(
                hostService.Processor.BuildSteps,
                buildStep =>
                {
                    Logger.LogVerbose($"Processor {hostService.Processor.Name}, step {buildStep.Name}: Prebuilding...");
                    using (new LoggerPhaseScope(buildStep.Name, true))
                    {
                        var models = buildStep.Prebuild(hostService.Models, hostService);
                        if (!object.ReferenceEquals(models, hostService.Models))
                        {
                            Logger.LogVerbose($"Processor {hostService.Processor.Name}, step {buildStep.Name}: Reloading models...");
                            hostService.Reload(models);
                        }
                    }
                });
        }

        private static void BuildArticle(HostService hostService, int maxParallelism)
        {
            hostService.Models.RunAll(
                m =>
                {
                    using (new LoggerFileScope(m.LocalPathFromRoot))
                    {
                        Logger.LogDiagnostic($"Processor {hostService.Processor.Name}: Building...");
                        BuildPhaseUtility.RunBuildSteps(
                            hostService.Processor.BuildSteps,
                            buildStep =>
                            {
                                Logger.LogDiagnostic($"Processor {hostService.Processor.Name}, step {buildStep.Name}: Building...");
                                using (new LoggerPhaseScope(buildStep.Name, true))
                                {
                                    buildStep.Build(m, hostService);
                                }
                            });
                    }
                },
                maxParallelism);
        }

        #endregion
    }
}
