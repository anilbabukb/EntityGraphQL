using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using EntityGraphQL.Extensions;

namespace EntityGraphQL.Compiler.Util
{
    public static class ExpressionUtil
    {
        public static ExpressionResult MakeExpressionCall(Type[] types, string methodName, Type[] genericTypes, params Expression[] parameters)
        {
            foreach (var t in types)
            {
                // Please tell me a better way to do this!
                try
                {
                    return (ExpressionResult)Expression.Call(t, methodName, genericTypes, parameters);
                }
                catch (InvalidOperationException)
                {
                    continue; // to next type
                }
            }
            var typesStr = string.Join<Type>(", ", types);
            throw new EntityGraphQLCompilerException($"Could not find extension method {methodName} on types {typesStr}");
        }

        public static MemberExpression CheckAndGetMemberExpression<TBaseType, TReturn>(Expression<Func<TBaseType, TReturn>> fieldSelection)
        {
            var exp = fieldSelection.Body;
            if (exp.NodeType == ExpressionType.Convert)
                exp = ((UnaryExpression)exp).Operand;

            if (exp.NodeType != ExpressionType.MemberAccess)
                throw new ArgumentException("fieldSelection should be a property or field accessor expression only. E.g (t) => t.MyField", "fieldSelection");
            return (MemberExpression)exp;
        }

        public static MemberExpression CheckAndGetMemberExpression<TBaseType, TArgType, TReturn>(Expression<Func<TBaseType, TArgType, TReturn>> fieldSelection)
        {
            var exp = fieldSelection.Body;
            if (exp.NodeType == ExpressionType.Convert)
                exp = ((UnaryExpression)exp).Operand;

            if (exp.NodeType != ExpressionType.MemberAccess)
                throw new ArgumentException("fieldSelection should be a property or field accessor expression only. E.g (t) => t.MyField", "fieldSelection");
            return (MemberExpression)exp;
        }

        public static object ChangeType(object value, Type type)
        {
            var objType = value.GetType();
            if (typeof(Newtonsoft.Json.Linq.JToken).IsAssignableFrom(objType))
            {
                var newVal = ((Newtonsoft.Json.Linq.JToken)value).ToObject(type);
                return newVal;
            }

            if (type != typeof(string) && objType == typeof(string))
            {
                if (type == typeof(double) || type == typeof(Nullable<double>))
                    return double.Parse((string)value);
                if (type == typeof(float) || type == typeof(Nullable<float>))
                    return float.Parse((string)value);
                if (type == typeof(int) || type == typeof(Nullable<int>))
                    return int.Parse((string)value);
                if (type == typeof(uint) || type == typeof(Nullable<uint>))
                    return uint.Parse((string)value);
            }
            var argumentNonNullType = type.IsNullableType() ? Nullable.GetUnderlyingType(type) : type;
            var valueNonNullType = objType.IsNullableType() ? Nullable.GetUnderlyingType(objType) : objType;
            if (argumentNonNullType.GetTypeInfo().IsEnum)
            {
                return Enum.ToObject(argumentNonNullType, value);
            }
            if (argumentNonNullType != valueNonNullType)
            {
                var newVal = Convert.ChangeType(value, argumentNonNullType);
                return newVal;
            }
            return value;
        }

        /// <summary>
        /// Trys to take 2 expressions returned from FindIEnumerable and join them together. I.e. If we Split list.First() with FindIEnumerable, we can join it back together with newList.First()
        /// </summary>
        /// <param name="baseExp"></param>
        /// <param name="nextExp"></param>
        /// <returns></returns>
        public static Expression CombineExpressions(Expression baseExp, Expression nextExp)
        {
            switch (nextExp.NodeType)
            {
                case ExpressionType.Call:
                    {
                        var mc = (MethodCallExpression)nextExp;
                        if (mc.Object == null)
                        {
                            var args = new List<Expression> { baseExp };
                            var newParam = Expression.Parameter(baseExp.Type.GetGenericArguments().First());
                            foreach (var item in mc.Arguments.Skip(1))
                            {
                                var lambda = (LambdaExpression)item;
                                var exp = new ParameterReplacer().Replace(lambda, lambda.Parameters.First(), newParam);
                                args.Add(exp);
                            }
                            var call = ExpressionUtil.MakeExpressionCall(new[] { typeof(Queryable), typeof(Enumerable) }, mc.Method.Name, baseExp.Type.GetGenericArguments().ToArray(), args.ToArray());
                            return call;
                        }
                        return Expression.Call(baseExp, mc.Method, mc.Arguments);
                    }
                default: throw new EntityGraphQLCompilerException($"Could not join expressions '{baseExp.NodeType} and '{nextExp.NodeType}'");
            }
        }

        /// <summary>
        /// Naviagtes back through an expression to see if there was a point where we had a IEnumerable object so we can "edit" it (so a .Select() etc.)
        /// </summary>
        /// <param name="baseExpression"></param>
        /// <returns></returns>
        public static Tuple<Expression, Expression> FindIEnumerable(Expression baseExpression)
        {
            var exp = baseExpression;
            Expression endExpression = null;
            while (exp != null && !exp.Type.IsEnumerableOrArray())
            {
                switch (exp.NodeType)
                {
                    case ExpressionType.Call:
                        {
                            endExpression = exp;
                            var mc = (MethodCallExpression)exp;
                            exp = mc.Object != null ? mc.Object : mc.Arguments.First();
                            break;
                        }
                    default:
                        exp = null;
                        break;
                }
            }
            return Tuple.Create(exp, endExpression);
        }

        public static Expression SelectDynamicToList(ParameterExpression currentContextParam, Expression baseExp, IEnumerable<IGraphQLNode> fieldExpressions)
        {
            var memberInit = CreateNewExpression(fieldExpressions, out Type dynamicType);
            var selector = Expression.Lambda(memberInit, currentContextParam);
            var call = MakeExpressionCall(new[] { typeof(Queryable), typeof(Enumerable) }, "Select", new Type[2] { currentContextParam.Type, dynamicType }, baseExp, selector);
            return call;
        }

        public static Expression CreateNewExpression(IEnumerable<IGraphQLNode> fieldExpressions)
        {
            var memberInit = CreateNewExpression(fieldExpressions, out _);
            return memberInit;
        }
        private static Expression CreateNewExpression(IEnumerable<IGraphQLNode> fieldExpressions, out Type dynamicType)
        {
            var fieldExpressionsByName = new Dictionary<string, ExpressionResult>();
            foreach (var item in fieldExpressions)
            {
                // if there are duplicate fields (looking at you ApolloClient when using fragments) they override
                fieldExpressionsByName[item.Name] = item.GetNodeExpression();
            }
            dynamicType = LinqRuntimeTypeBuilder.GetDynamicType(fieldExpressionsByName.ToDictionary(f => f.Key, f => f.Value.Type));

            var bindings = dynamicType.GetFields().Select(p => Expression.Bind(p, fieldExpressionsByName[p.Name])).OfType<MemberBinding>();
            var newExp = Expression.New(dynamicType.GetConstructor(Type.EmptyTypes));
            var mi = Expression.MemberInit(newExp, bindings);
            return mi;
        }
        public static Tuple<Expression, Expression> FindDistinct(ExpressionResult expressionResult)
        {
            Expression expr = null;
            Expression endExpression = null;

            if (expressionResult.Expression is MethodCallExpression methodCallExpression)
            {
                var methodInfo = methodCallExpression.Method;
                var methodName = methodInfo.Name;
                if (methodName == "Distinct")
                {
                    var exp = expressionResult.Expression;
                    //while (exp != null)
                    //{
                    switch (exp.NodeType)
                    {
                        case ExpressionType.Call:
                            {
                                endExpression = exp;
                                var mc = (MethodCallExpression)exp;
                                exp = mc.Object != null ? mc.Object : mc.Arguments.First();
                                break;
                            }
                        default:
                            //exp = null;
                            break;
                    }
                    //}
                    expr = exp;
                }
            }

            return Tuple.Create(expr, endExpression);
        }
    }
}