using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Microsoft.CodeAnalysis.Tools.Analyzers
{
    internal class CompositeCodeFixProvider : CodeFixProvider
    {
        private readonly ImmutableArray<CodeFixProvider> _fixes;

        internal CompositeCodeFixProvider(ImmutableArray<CodeFixProvider> fixes)
        {
            FixableDiagnosticIds = fixes.SelectMany(f => f.FixableDiagnosticIds).ToImmutableArray();
            _fixes = fixes;
        }

        public override ImmutableArray<string> FixableDiagnosticIds { get; }

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var registerTasks = new Task[_fixes.Length];
            for (int i = 0; i < _fixes.Length; i++)
            {
                registerTasks[i] = Task.Run(() => _fixes[i].RegisterCodeFixesAsync(context), context.CancellationToken);
            }
            return Task.WhenAll(registerTasks);
        }

        public override FixAllProvider GetFixAllProvider()
        {
            // todo: delegate to underlying FixAllProviders internally, then pull them together with the batch fixer?
            // not clear if that would actually be better
            return WellKnownFixAllProviders.BatchFixer;
        }
    }

    internal class CompositeFixAllProvider : FixAllProvider
    {
        private readonly ImmutableArray<CodeFixProvider> _fixes;

        internal CompositeFixAllProvider(ImmutableArray<CodeFixProvider> fixes)
        {
            _fixes = fixes;
        }

        public override Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            // TODO
            foreach (var fix in _fixes)
            {
                fix.GetFixAllProvider().GetFixAsync(fixAllContext);
            }
            return WellKnownFixAllProviders.BatchFixer.GetFixAsync(fixAllContext);
        }
    }
}
