using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Bonsai.Expressions;

namespace Bonsai.Zarr.Tests
{
    static class WorkflowTestHelper
    {
        public static IObservable<TResult> BuildObservable<TResult>(Expression source, params ExpressionBuilder[] builders)
        {
            var workflow = new ExpressionBuilderGraph();
            var node = workflow.Add(new SourceExpressionBuilder(source));
            foreach (var builder in builders.Concat(new[] { new WorkflowOutputBuilder() }))
            {
                var successor = workflow.Add(builder);
                workflow.AddEdge(node, successor, new ExpressionBuilderArgument());
                node = successor;
            }

            return workflow.BuildObservable<TResult>();
        }

        class SourceExpressionBuilder : ZeroArgumentExpressionBuilder
        {
            readonly Expression expression;

            public SourceExpressionBuilder(Expression expression)
            {
                this.expression = expression;
            }

            public override Expression Build(IEnumerable<Expression> arguments)
            {
                return expression;
            }
        }
    }
}
