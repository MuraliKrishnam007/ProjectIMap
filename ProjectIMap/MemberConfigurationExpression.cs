using System;
using System.Linq.Expressions;

namespace ProjectIMap
{
    /// <summary>
    /// Internal implementation of <see cref="IMemberConfigurationExpression{TSource}"/>.
    /// Delegates every instruction directly to <see cref="MapperConfiguration"/> so
    /// the override is visible to <see cref="Mapper"/>'s Expression Tree compiler.
    /// </summary>
    internal sealed class MemberConfigurationExpression<TSource>
        : IMemberConfigurationExpression<TSource>
    {
        private readonly MapperConfiguration _configuration;
        private readonly Type _destType;
        private readonly string _destPropertyName;

        internal MemberConfigurationExpression(
            MapperConfiguration configuration,
            Type                destType,
            string              destPropertyName)
        {
            _configuration    = configuration;
            _destType         = destType;
            _destPropertyName = destPropertyName;
        }

        /// <inheritdoc/>
        public void Ignore()
            => Override().IsIgnored = true;

        /// <inheritdoc/>
        public void MapFrom<TMember>(Expression<Func<TSource, TMember>> sourceMember)
            => Override().MapFromExpression = sourceMember;

        /// <inheritdoc/>
        public void MapFrom<TMember>(IValueResolver<TSource, TMember> resolver)
        {
            // Build `source => resolverConstant.Resolve(source)` and store it in the
            // same MapFromExpression slot an inline lambda would use — Phase 0 in
            // Mapper/ProjectionCompiler already knows how to inline and combine it
            // with Condition/NullSubstitute without any further changes.
            var sourceParam   = Expression.Parameter(typeof(TSource), "source");
            var resolverConst = Expression.Constant(resolver, typeof(IValueResolver<TSource, TMember>));
            var resolveMethod = typeof(IValueResolver<TSource, TMember>).GetMethod(
                nameof(IValueResolver<TSource, TMember>.Resolve))!;
            var call   = Expression.Call(resolverConst, resolveMethod, sourceParam);
            var lambda = Expression.Lambda<Func<TSource, TMember>>(call, sourceParam);

            Override().MapFromExpression = lambda;
        }

        /// <inheritdoc/>
        public void MapFrom<TResolver, TMember>() where TResolver : IValueResolver<TSource, TMember>
        {
            var @override = Override();
            @override.ResolverType       = typeof(TResolver);
            @override.ResolverMemberType = typeof(TMember);
        }

        /// <inheritdoc/>
        public void Condition(Expression<Func<TSource, bool>> condition)
            => Override().ConditionExpression = condition;

        /// <inheritdoc/>
        public void NullSubstitute<TMember>(TMember substitute)
        {
            var @override = Override();
            @override.HasNullSubstitute  = true;
            @override.NullSubstituteValue = substitute;
            @override.NullSubstituteType  = typeof(TMember);
        }

        private MemberOverride Override()
            => _configuration.GetOrCreateMemberOverride(typeof(TSource), _destType, _destPropertyName);
    }
}
