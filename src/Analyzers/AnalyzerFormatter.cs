// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

# nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffMatchPatch;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Tools.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.CodingConventions;

namespace Microsoft.CodeAnalysis.Tools.Analyzers
{
    class AnalyzerFormatter : ICodeFormatter
    {
        public FormatType FormatType => FormatType.CodeStyle;

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
            var analysisStopwatch = Stopwatch.StartNew();
            logger.LogTrace($"Analyzing code style.");


            if (!options.SaveFormattedFiles)
            {
                await LogDiagnosticsAsync(solution, formattableDocuments, options, logger, cancellationToken);
            }
            else
            {
                solution = await FixDiagnosticsAsync(solution, formattableDocuments, logger, cancellationToken);
            }

            logger.LogTrace("Analysis complete in {0}ms.", analysisStopwatch.ElapsedMilliseconds);

            return solution;
        }

        private async Task LogDiagnosticsAsync(Solution solution, ImmutableArray<(Document, OptionSet, ICodingConventionsSnapshot)> formattableDocuments, FormatOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            var pairs = _finder.GetAnalyzersAndFixers();
            var paths = formattableDocuments.Select(x => x.Item1.FilePath).ToImmutableArray();

            // no need to run codefixes as we won't persist the changes
            var analyzers = pairs.Select(x => x.Analyzer).ToImmutableArray();
            var result = new CodeAnalysisResult();
            await solution.Projects.ForEachAsync(async (project, token) =>
            {
                var options = _finder.GetWorkspaceAnalyzerOptions(project);
                await _runner.RunCodeAnalysisAsync(result, analyzers, project, options, paths, logger, token);
            }, cancellationToken);

            LogDiagnosticLocations(result.Diagnostics.SelectMany(kvp => kvp.Value), options.WorkspaceFilePath, options.ChangesAreErrors, logger);

            return;

            static void LogDiagnosticLocations(IEnumerable<Diagnostic> diagnostics, string workspacePath, bool changesAreErrors, ILogger logger)
            {
                var workspaceFolder = Path.GetDirectoryName(workspacePath);

                foreach (var diagnostic in diagnostics)
                {
                    var message = diagnostic.GetMessage();
                    var filePath = diagnostic.Location.SourceTree.FilePath;

                    var mappedLineSpan = diagnostic.Location.GetMappedLineSpan();
                    var changePosition = mappedLineSpan.StartLinePosition;

                    var formatMessage = $"{Path.GetRelativePath(workspaceFolder, filePath)}({changePosition.Line + 1},{changePosition.Character + 1}): {message}";

                    if (changesAreErrors)
                    {
                        logger.LogError(formatMessage);
                    }
                    else
                    {
                        logger.LogWarning(formatMessage);
                    }
                }
            }
        }

        private async Task<Solution> FixDiagnosticsAsync(Solution solution, ImmutableArray<(Document, OptionSet, ICodingConventionsSnapshot)> formattableDocuments, ILogger logger, CancellationToken cancellationToken)
        {
            var pairs = _finder.GetAnalyzersAndFixers();
            var paths = formattableDocuments.Select(x => x.Item1.FilePath).ToImmutableArray();

            var result = new CodeAnalysisResult();
            var analyzers = pairs.Select(pair => pair.Analyzer).ToImmutableArray();
            await Task.WhenAll(solution.Projects.Select(project =>
            {
                var options = _finder.GetWorkspaceAnalyzerOptions(project);
                return Task.Run(() => _runner.RunCodeAnalysisAsync(result, analyzers, project, options, paths, logger, cancellationToken));
            }).ToArray());

            var documentVersions = new Dictionary<DocumentId, List<Document>>();

            var changedSolutions = await Task.WhenAll(
                pairs.Select(async pair =>
                {
                    logger.LogTrace($"Applying fixes for {pair.Fixer.GetType().Name}");
                    try
                    {
                        return await _applier.ApplyCodeFixesAsync(solution, result, pair.Fixer, logger, cancellationToken);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e.Message);
                        return solution;
                    }
                }).ToArray());

            foreach (var changedSolution in changedSolutions)
            {
                var changes = changedSolution.GetChanges(solution);
                foreach (var projectChanges in changes.GetProjectChanges())
                {
                    var changedDocuments = projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true);
                    foreach (var id in changedDocuments)
                    {
                        if (!documentVersions.ContainsKey(id))
                        {
                            documentVersions[id] = new List<Document>();
                        }

                        var changedDocument = changedSolution.GetDocument(id)!;
                        documentVersions[id].Add(changedDocument);
                    }
                }
            }

            var mergedSolution = solution;
            var dmp = new diff_match_patch();
            // TODO: merge documents, re-run serially to handle conflicts
            foreach (var (id, docs) in documentVersions)
            {
                while (docs.Count >= 2)
                {
                    // TODO: hoist some of this stuff?
                    var originalText = await solution.GetDocument(id)!.GetTextAsync(cancellationToken);
                    var originalString = originalText.ToString();

                    var (text1, text2) = (await docs[docs.Count - 1].GetTextAsync(cancellationToken), await docs[docs.Count - 2].GetTextAsync(cancellationToken));
                    var (string1, string2) = (text1.ToString(), text2.ToString());

                    var patch1 = dmp.patch_make(originalString, string1);
                    var patch2 = dmp.patch_make(originalString, string2);

                    foreach (var patchItem in patch2)
                    {
                        // TODO: ordering?
                        patch1.Add(patchItem);
                    }

                    object[] patched = dmp.patch_apply(patch1, originalString);
                    var (patchedText, wasPatchApplied) = ((string)patched[0], (bool[])patched[1]);

                    for (var i = 0; i < wasPatchApplied.Length; i++)
                    {
                        if (!wasPatchApplied[i])
                        {
                            // TODO: flag file as needing serial analysis/fixing
                            logger.LogWarning("Didn't apply part of the patch" + patch1[i]);
                        }
                    }

                    var newText = SourceText.From(patchedText, originalText.Encoding, originalText.ChecksumAlgorithm);
                    var newDoc = docs[docs.Count - 1].WithText(newText);
                    docs[docs.Count - 2] = newDoc;
                    docs.RemoveAt(docs.Count - 1);
                    mergedSolution = mergedSolution.WithDocumentText(id, newText);
                }
            }

            return mergedSolution;
        }
    }
}
