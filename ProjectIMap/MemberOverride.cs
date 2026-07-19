using System.Linq.Expressions;

namespace ProjectIMap
{
    /// <summary>
    /// Stores the resolved override instructions for a single destination property,
    /// accumulated by <see cref="IMemberConfigurationExpression{TSource}"/> calls and
    /// consumed by <see cref="Mapper"/>/<see cref="ProjectionCompiler"/> at compile time.
    /// </summary>
    /// <remarks>
    /// Mutable by design: a single <c>.ForMember(d => d.X, opt => { ... })</c> callback
    /// may call more than one option (e.g. <c>MapFrom</c> followed by <c>Condition</c>)
    /// against the same destination property, and each call must accumulate onto the
    /// same override rather than replacing it.
    /// </remarks>
    internal sealed class MemberOverride
    {
        /// <summary>
        /// When <see langword="true"/> the destination property is skipped entirely
        /// and no binding/assignment is emitted.
        /// </summary>
        public bool IsIgnored { get; internal set; }

        /// <summary>
        /// A <see cref="LambdaExpression"/> whose single parameter is the source
        /// object and whose body produces the value for the destination property.
        /// </summary>
        public LambdaExpression? MapFromExpression { get; internal set; }

        /// <summary>
        /// A <see cref="LambdaExpression"/> whose single parameter is the source
        /// object and whose body is a <see cref="bool"/> predicate. When present,
        /// the destination member is only written when the predicate evaluates to
        /// <see langword="true"/>.
        /// </summary>
        public LambdaExpression? ConditionExpression { get; internal set; }

        /// <summary>
        /// <see langword="true"/> when <see cref="NullSubstituteValue"/> should be
        /// used in place of a <see langword="null"/> resolved source value.
        /// </summary>
        public bool HasNullSubstitute { get; internal set; }

        /// <summary>The substitute value to use when the resolved source value is null.</summary>
        public object? NullSubstituteValue { get; internal set; }

        /// <summary>The static type of <see cref="NullSubstituteValue"/>.</summary>
        public System.Type? NullSubstituteType { get; internal set; }

        /// <summary>
        /// The <see cref="IValueResolver{TSource,TMember}"/> implementation to resolve
        /// via the <see cref="Mapper"/>'s <see cref="System.IServiceProvider"/> at map
        /// time, set by the type-only <c>MapFrom&lt;TResolver,TMember&gt;()</c> overload.
        /// Distinct from <see cref="MapFromExpression"/>, which already covers inline
        /// lambdas and caller-constructed resolver instances.
        /// </summary>
        public System.Type? ResolverType { get; internal set; }

        /// <summary>The <c>TMember</c> produced by <see cref="ResolverType"/>'s <c>Resolve</c> method.</summary>
        public System.Type? ResolverMemberType { get; internal set; }
    }
}
