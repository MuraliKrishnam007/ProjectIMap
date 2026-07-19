using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ProjectIMap
{
    /// <summary>
    /// LINQ extension methods that bridge <see cref="ProjectionCompiler"/> with EF Core
    /// (or any <see cref="IQueryProvider"/> implementation).
    /// </summary>
    public static class QueryableExtensions
    {
        /// <summary>
        /// Projects each element of <paramref name="source"/> to
        /// <typeparamref name="TDestination"/> using a pure
        /// <c>Expression&lt;Func&lt;TSource, TDestination&gt;&gt;</c> built from the
        /// registered mappings in <paramref name="config"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The projection lambda is built by <see cref="ProjectionCompiler.BuildProjection"/>
        /// and cached for the lifetime of the process; subsequent calls for the same
        /// type pair incur only a dictionary lookup.
        /// </para>
        /// <para>
        /// The lambda is <b>never compiled to a delegate</b>. It is passed to the
        /// underlying <see cref="IQueryProvider"/> as a quoted expression tree so that
        /// EF Core (or any other LINQ provider) can translate it directly to SQL.
        /// </para>
        /// </remarks>
        /// <typeparam name="TDestination">The destination / DTO type.</typeparam>
        /// <param name="source">
        /// The <see cref="IQueryable"/> to project. Must not be <see langword="null"/>.
        /// </param>
        /// <param name="config">
        /// The <see cref="MapperConfiguration"/> that contains the registered mapping
        /// for <c>source.ElementType → TDestination</c>.
        /// </param>
        /// <returns>
        /// An <see cref="IQueryable{TDestination}"/> backed by the provider's query
        /// pipeline. For EF Core this translates to an optimised SQL SELECT.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="source"/> or <paramref name="config"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no mapping from <c>source.ElementType</c> to
        /// <typeparamref name="TDestination"/> has been registered.
        /// </exception>
        public static IQueryable<TDestination> ProjectTo<TDestination>(
            this IQueryable    source,
            MapperConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(config);

            var sourceType = source.ElementType;
            var destType   = typeof(TDestination);

            // Retrieve (or build and cache) the pure expression tree lambda.
            // The returned LambdaExpression is an Expression<Func<TSource,TDest>>
            // at runtime; ProjectionCompiler guarantees this via Expression.Lambda(delegateType,...).
            var lambda = ProjectionCompiler.BuildProjection(sourceType, destType, config);

            // Build the Queryable.Select call expression and hand it to the provider.
            //
            // We construct the expression tree for the Select call manually rather
            // than invoking source.Select(lambda) so that:
            //   1. The lambda is never compiled to a Func — it remains an expression.
            //   2. The call flows through IQueryProvider.CreateQuery which is exactly
            //      what EF Core intercepts to build SQL.
            //
            // Expression.Quote wraps the lambda in a UnaryExpression<ExpressionType.Quote>,
            // which is the standard way LINQ providers receive typed lambda parameters.
            var selectMethod = GetSelectMethod(sourceType, destType);

            var selectCallExpr = Expression.Call(
                selectMethod,
                source.Expression,
                Expression.Quote(lambda));

            return source.Provider.CreateQuery<TDestination>(selectCallExpr);
        }

        // ── Helper: locate Queryable.Select<TSource,TResult>(IQueryable<TSource>, Expression<Func<TSource,TResult>>) ──

        private static MethodInfo GetSelectMethod(Type sourceType, Type destType)
        {
            // Queryable.Select has two overloads — we want the one whose second
            // parameter is Expression<Func<TSource, TResult>> (index-free projection).
            var method = typeof(Queryable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m =>
                {
                    if (m.Name != nameof(Queryable.Select)) return false;
                    var ps = m.GetParameters();
                    if (ps.Length != 2)  return false;

                    // The projection overload's second parameter is Expression<Func<T,R>>,
                    // which has one generic argument in the Func. The index overload uses
                    // Expression<Func<T,int,R>> which has two Func generic arguments.
                    var p1Type = ps[1].ParameterType;
                    if (!p1Type.IsGenericType) return false;

                    var innerDelegate = p1Type.GetGenericArguments()[0];
                    return innerDelegate.IsGenericType
                        && innerDelegate.GetGenericTypeDefinition() == typeof(Func<,>);
                })
                .MakeGenericMethod(sourceType, destType);

            return method;
        }
    }
}
