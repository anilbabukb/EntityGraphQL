using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace EntityQueryLanguage.Compiler
{
    public class MutationResult : ExpressionResult
    {
        private string method;
        private readonly Schema.MutationType mutationType;
        private readonly Expression paramExp;
        private Dictionary<string, ExpressionResult> gqlRequestArgs;

        public MutationResult(string method, Schema.MutationType mutationType, Dictionary<string, ExpressionResult> args) : base(null)
        {
            this.method = method;
            this.mutationType = mutationType;
            this.gqlRequestArgs = args;
            paramExp = Expression.Parameter(mutationType.ContextType);
        }

        public override Expression Expression { get { return paramExp; } }

        public object Execute(object[] externalArgs)
        {
            return mutationType.Call(externalArgs, gqlRequestArgs);
        }
    }
}