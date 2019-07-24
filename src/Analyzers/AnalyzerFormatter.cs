﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

# nullable enable

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Tools.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.CodingConventions;

namespace Microsoft.CodeAnalysis.Tools.Analyzers
{
    class AnalyzerFormatter : ICodeFormatter
    {
        private readonly IAnalyzerFinder _finder;
        private readonly IAnalyzerRunner _runner;
        private readonly ICodeFixApplier _applier;

        public AnalyzerFormatter(IAnalyzerFinder finder,
                                 IAnalyzerRunner runner,
                                 ICodeFixApplier applier)
        {
            _finder = finder;
            _runner = runner;
            _applier = applier;
        }

        public async Task<Solution> FormatAsync(Solution solution,
                                                ImmutableArray<(Document, OptionSet, ICodingConventionsSnapshot)> formattableDocuments,
                                                FormatOptions options,
                                                ILogger logger,
                                                CancellationToken cancellationToken)
        {
            if (!options.FormatType.HasFlag(FormatType.CodeStyle))
            {
                return solution;
            }


            var pairs = _finder.GetAnalyzersAndFixers();
            var analyzers = pairs.Select(pair => pair.Analyzer).ToImmutableArray();
            var paths = formattableDocuments.Select(x => x.Item1.FilePath).ToImmutableArray();
            var result = new CodeAnalysisResult();
            await solution.Projects.ForEachAsync(async (project, token) =>
            {
                var options = _finder.GetWorkspaceAnalyzerOptions(project);
                await _runner.RunCodeAnalysisAsync(result, analyzers, project, options, paths, logger, token);
            }, cancellationToken);

            var codefix = new CompositeCodeFixProvider(pairs.Select(p => p.Fixer).ToImmutableArray());
            solution = await _applier.ApplyCodeFixesAsync(solution, result, codefix, logger, cancellationToken);

            return solution;
        }
    }
}
