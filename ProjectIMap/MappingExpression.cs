using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ProjectIMap
{
    /// <summary>
    /// Internal implementation of <see cref="IMappingExpression{TSource,TDestination}"/>.
    /// Holds a back-reference to the <see cref="MapperConfiguration"/> so that
    /// <see cref="ReverseMap"/> can register the inverse pair and
    /// <see cref="ForMember"/> can store member overrides.
    /// </summary>
    internal sealed class MappingExpression<TSource, TDestination>
        : IMappingExpression<TSource, TDestination>
    {
        private readonly MapperConfiguration _configuration;

        internal MappingExpression(MapperConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <inheritdoc/>
        public IMappingExpression<TDestination, TSource> ReverseMap()
        {
            _configuration.RegisterMap<TDestination, TSource>();
            return new MappingExpression<TDestination, TSource>(_configuration);
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> ForMember(
            Expression<Func<TDestination, object>> destinationMember,
            Action<IMemberConfigurationExpression<TSource>> options)
        {
            var propName     = ResolveMemberName(destinationMember);
            var memberConfig = new MemberConfigurationExpression<TSource>(
                _configuration, typeof(TDestination), propName);
            options(memberConfig);
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> ConstructUsing(
            Expression<Func<TSource, TDestination>> ctor)
        {
            _configuration.SetCustomConstructor(typeof(TSource), typeof(TDestination), ctor);
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> action)
        {
            _configuration.SetBeforeMap(typeof(TSource), typeof(TDestination), action);
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action)
        {
            _configuration.SetAfterMap(typeof(TSource), typeof(TDestination), action);
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> MaxDepth(int depth)
        {
            if (depth < 1)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "MaxDepth must be at least 1.");

            _configuration.SetMaxDepth(typeof(TSource), typeof(TDestination), depth);
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> FlattenDepth(int depth)
        {
            if (depth < 1)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "FlattenDepth must be at least 1.");

            _configuration.SetFlattenDepth(typeof(TSource), typeof(TDestination), depth);
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
            where TDerivedSource : TSource
            where TDerivedDestination : TDestination
        {
            _configuration.AddInclude(
                typeof(TSource), typeof(TDestination), typeof(TDerivedSource), typeof(TDerivedDestination));
            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> ForAllMembers(
            Action<IMemberConfigurationExpression<TSource>> configure)
        {
            var destType = typeof(TDestination);

            var destProps = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

            foreach (var destProp in destProps)
            {
                if (_configuration.TryGetMemberOverride(typeof(TSource), destType, destProp.Name, out _))
                    continue; // an explicit .ForMember(...) override always wins, regardless of call order

                var memberConfig = new MemberConfigurationExpression<TSource>(_configuration, destType, destProp.Name);
                configure(memberConfig);
            }

            return this;
        }

        /// <inheritdoc/>
        public IMappingExpression<TSource, TDestination> EqualityComparison(Func<TSource, TDestination, bool> comparer)
        {
            _configuration.SetEqualityComparer(typeof(TSource), typeof(TDestination), comparer);
            return this;
        }

        /// <summary>
        /// Extracts the property name from a member-selector lambda.
        /// Handles the <see cref="UnaryExpression"/> box that the compiler inserts
        /// when the selected property is a value type (e.g. <c>dest => dest.Age</c>
        /// becomes <c>Convert(dest.Age, Object)</c>).
        /// </summary>
        private static string ResolveMemberName(LambdaExpression lambda)
        {
            var body = lambda.Body;

            // Strip the boxing Convert that the C# compiler adds for value types.
            if (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
                body = unary.Operand;

            if (body is MemberExpression memberExpr)
                return memberExpr.Member.Name;

            throw new ArgumentException(
                "ForMember requires a simple property selector, e.g. dest => dest.PropertyName.",
                nameof(lambda));
        }
    }
}
