using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using EntityGraphQL.Grammer;
using EntityGraphQL.Extensions;
using EntityGraphQL.Schema;
using EntityGraphQL.LinqQuery;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace EntityGraphQL.Compiler
{

    internal class EntityQueryNodeVisitor : EntityGraphQLBaseVisitor<ExpressionResult>
    {
        private readonly ClaimsIdentity claims;
        private ExpressionResult currentContext;
        private readonly ISchemaProvider schemaProvider;
        private readonly IMethodProvider methodProvider;
        
        private readonly Regex guidRegex = new Regex(@"^[0-9A-F]{8}[-]?([0-9A-F]{4}[-]?){3}[0-9A-F]{12}$", RegexOptions.IgnoreCase);        
        private readonly ConstantVisitor constantVisitor;

        public EntityQueryNodeVisitor(Expression expression, ISchemaProvider schemaProvider, IMethodProvider methodProvider, QueryVariables variables, ClaimsIdentity claims)
        {
            this.claims = claims;
            currentContext = (ExpressionResult)expression;
            this.schemaProvider = schemaProvider;
            this.methodProvider = methodProvider;
            this.constantVisitor = new ConstantVisitor(schemaProvider);
        }

        public override ExpressionResult VisitBinary(EntityGraphQLParser.BinaryContext context)
        {
            var left = Visit(context.left);
            var right = Visit(context.right);
            var op = MakeOperator(context.op.GetText());
            // we may need to do some converting here
            if (left.Type != right.Type)
            {
                if (op == ExpressionType.Equal || op == ExpressionType.NotEqual)
                {
                    var result = DoObjectComparisonOnDifferentTypes(op, left, right);

                    if (result != null)
                        return result;
                }
                return ConvertLeftOrRight(op, left, right);
            }

            if (left.Type == typeof(string) && right.Type == typeof(string))
            {
                if(op == ExpressionType.Add)
                    return (ExpressionResult)Expression.Call(null, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }), left, right);

                if (op == ExpressionType.GreaterThan || op == ExpressionType.GreaterThanOrEqual || op == ExpressionType.LessThan || op == ExpressionType.LessThanOrEqual)
                {
                    MethodInfo methodInfo = typeof(string).GetMethod("CompareTo", new[] { typeof(string) });
                    var callExpr = Expression.Call(left, methodInfo, right);
                    ExpressionResult searchExpr = null;
                    if(op == ExpressionType.GreaterThan)
                        searchExpr = (ExpressionResult)Expression.GreaterThan(callExpr, Expression.Constant(0));
                    if (op == ExpressionType.GreaterThanOrEqual)
                        searchExpr = (ExpressionResult)Expression.GreaterThanOrEqual(callExpr, Expression.Constant(0));
                    if (op == ExpressionType.LessThan)
                        searchExpr = (ExpressionResult)Expression.LessThan(callExpr, Expression.Constant(0));
                    if (op == ExpressionType.LessThanOrEqual)
                        searchExpr = (ExpressionResult)Expression.LessThanOrEqual(callExpr, Expression.Constant(0));
                    return searchExpr;
                }
            }

            return (ExpressionResult)Expression.MakeBinary(op, left, right);
        }

        private ExpressionResult DoObjectComparisonOnDifferentTypes(ExpressionType op, ExpressionResult left, ExpressionResult right)
        {
            var convertedToSameTypes = false;

            // leftGuid == 'asdasd' == null ? (Guid) null : new Guid('asdasdas'.ToString())
            // leftGuid == null
            if (left.Type == typeof(Guid) && right.Type != typeof(Guid))
            {
                right = ConvertToGuid(right);
                convertedToSameTypes = true;
            }
            else if (right.Type == typeof(Guid) && left.Type != typeof(Guid))
            {
                left = ConvertToGuid(left);
                convertedToSameTypes = true;
            }

            return convertedToSameTypes ? (ExpressionResult)Expression.MakeBinary(op, left, right) : null;
        }

        private static ExpressionResult ConvertToGuid(ExpressionResult expression)
        {
            return (ExpressionResult)Expression.Call(typeof(Guid), "Parse", null, (ExpressionResult)Expression.Call(expression, typeof(object).GetMethod("ToString")));
        }

        private static ExpressionResult ConvertToDate(ExpressionResult expression)
        {
            return (ExpressionResult)Expression.Call(typeof(DateTime), "Parse", null, (ExpressionResult)Expression.Call(expression, typeof(object).GetMethod("ToString")));
        }

        public override ExpressionResult VisitExpr(EntityGraphQLParser.ExprContext context)
        {
            var r = Visit(context.body);
            return r;
        }

        public override ExpressionResult VisitCallPath(EntityGraphQLParser.CallPathContext context)
        {
            var startingContext = currentContext;
            ExpressionResult exp = null;
            foreach (var child in context.children)
            {
                var r = Visit(child);
                if (r == null)
                    continue;

                if (exp != null)
                {
                    r.AddConstantParameters(exp.ConstantParameters);
                    r.AddServices(exp.Services);
                }
                exp = r;
                currentContext = exp;
            }
            currentContext = startingContext;
            return exp;
        }

        public override ExpressionResult VisitIdentity(EntityGraphQLParser.IdentityContext context)
        {
            var field = context.GetText();
            string name = schemaProvider.GetSchemaTypeNameForDotnetType(currentContext.Type);
            if (!schemaProvider.TypeHasField(name, field, null, claims))
            {
                throw new EntityGraphQLCompilerException($"Field {field} not found on type {name}");
            }
            var exp = schemaProvider.GetExpressionForField(currentContext, name, field, null, claims);
            return exp;

        }

        public override ExpressionResult VisitConstant(EntityGraphQLParser.ConstantContext context)
        {
            //// we may need to convert a string into a DateTime or Guid type
            //string value = context.GetText().Substring(1, context.GetText().Length - 2).Replace("\\\"", "\"");
            //if (guidRegex.IsMatch(value))
            //    return (ExpressionResult)Expression.Constant(Guid.Parse(value));
            //if (IsValidDate(value))
            //    return (ExpressionResult)Expression.Constant(DateTime.Parse(value));
            //return (ExpressionResult)Expression.Constant(value);
            return constantVisitor.VisitConstant(context);
        }

        bool IsValidDate(string text)
        {
            try
            {
                DateTime.Parse(text);
                return true;
            }
            catch
            {
                return false;
            }
        }


        public override ExpressionResult VisitIfThenElse(EntityGraphQLParser.IfThenElseContext context)
        {
            return (ExpressionResult)Expression.Condition(CheckConditionalTest(Visit(context.test)), Visit(context.ifTrue), Visit(context.ifFalse));
        }

        public override ExpressionResult VisitIfThenElseInline(EntityGraphQLParser.IfThenElseInlineContext context)
        {
            return (ExpressionResult)Expression.Condition(CheckConditionalTest(Visit(context.test)), Visit(context.ifTrue), Visit(context.ifFalse));
        }

        public override ExpressionResult VisitCall(EntityGraphQLParser.CallContext context)
        {
            var method = context.method.GetText();
            if (!methodProvider.EntityTypeHasMethod(currentContext.Type, method))
            {
                throw new EntityGraphQLCompilerException($"Method '{method}' not found on current context '{currentContext.Type.Name}'");
            }
            // Keep the current context
            var outerContext = currentContext;
            // some methods might have a different inner context (IEnumerable etc)
            var methodArgContext = methodProvider.GetMethodContext(currentContext, method);
            currentContext = methodArgContext;
            // Compile the arguments with the new context
            var args = context.arguments?.children.Select(c => Visit(c)).ToList();
            // build our method call
            var call = methodProvider.MakeCall(outerContext, methodArgContext, method, args);
            currentContext = call;
            return call;
        }

        public override ExpressionResult VisitArgs(EntityGraphQLParser.ArgsContext context)
        {
            return VisitChildren(context);
        }

        /// Implements rules about comparing non-matching types.
        /// Nullable vs. non-nullable - the non-nullable gets converted to nullable
        /// int vs. uint - the uint gets down cast to int
        /// more to come...
        private ExpressionResult ConvertLeftOrRight(ExpressionType op, ExpressionResult left, ExpressionResult right)
        {
            if (left.Type.IsNullableType() && !right.Type.IsNullableType())
                right = (ExpressionResult)Expression.Convert(right, left.Type);
            else if (right.Type.IsNullableType() && !left.Type.IsNullableType())
                left = (ExpressionResult)Expression.Convert(left, right.Type);

            else if (left.Type == typeof(int) && (right.Type == typeof(uint) || right.Type == typeof(Int16) || right.Type == typeof(Int64) || right.Type == typeof(UInt16) || right.Type == typeof(UInt64)))
                right = (ExpressionResult)Expression.Convert(right, left.Type);
            else if (left.Type == typeof(uint) && (right.Type == typeof(int) || right.Type == typeof(Int16) || right.Type == typeof(Int64) || right.Type == typeof(UInt16) || right.Type == typeof(UInt64)))
                left = (ExpressionResult)Expression.Convert(left, right.Type);            
            else if(left.Type.IsEnum() && (right.Type == typeof(uint) || right.Type == typeof(Int16) || right.Type == typeof(Int64) || right.Type == typeof(UInt16) || right.Type == typeof(UInt64)))
                right = (ExpressionResult)Expression.Convert(right, left.Type);
            else if(left.Type.IsEnum() && right.Type == typeof(string))
            {
                string val = Expression.Lambda<Func<String>>(right.Expression).Compile()();
                Type enumType = left.Type;
                object t = Enum.Parse(enumType, val);
                if (t != null)
                {
                    right = new ExpressionResult(Expression.Constant((int)t,typeof(int)));                    
                    right = (ExpressionResult)Expression.Convert(right, left.Type);
                }                    
            }

            return (ExpressionResult)Expression.MakeBinary(op, left, right);
        }

        private Expression CheckConditionalTest(Expression test)
        {
            if (test.Type != typeof(bool))
                throw new EntityGraphQLCompilerException($"Expected boolean value in conditional test but found '{test}'");
            return test;
        }

        private ExpressionType MakeOperator(string op)
        {
            switch (op)
            {
                case "=": return ExpressionType.Equal;
                case "+": return ExpressionType.Add;
                case "-": return ExpressionType.Subtract;
                case "%": return ExpressionType.Modulo;
                case "^": return ExpressionType.Power;
                case "and": return ExpressionType.AndAlso;
                case "*": return ExpressionType.Multiply;
                case "or": return ExpressionType.OrElse;
                case "<=": return ExpressionType.LessThanOrEqual;
                case ">=": return ExpressionType.GreaterThanOrEqual;
                case "<": return ExpressionType.LessThan;
                case ">": return ExpressionType.GreaterThan;
                case "!=": return ExpressionType.NotEqual;
                default: throw new EntityGraphQLCompilerException($"Unsupported binary operator '{op}'");
            }
        }
    }
}