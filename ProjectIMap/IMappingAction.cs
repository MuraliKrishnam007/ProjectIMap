namespace ProjectIMap
{
    /// <summary>
    /// A reusable, dependency-injectable lifecycle hook attachable to a mapping
    /// pair via <see cref="IMappingExpression{TSource,TDestination}.BeforeMap{TAction}"/>
    /// or <see cref="IMappingExpression{TSource,TDestination}.AfterMap{TAction}"/> —
    /// the class-based alternative to an inline hook delegate, mirroring how
    /// <see cref="IValueResolver{TSource,TMember}"/> relates to an inline
    /// <c>MapFrom</c> lambda.
    /// </summary>
    /// <remarks>
    /// A fresh instance is resolved from the <see cref="Mapper"/>'s
    /// <see cref="System.IServiceProvider"/> on <b>every</b> map call, which is the
    /// correct behaviour for actions with scoped or transient dependencies. The
    /// <see cref="Mapper"/> must therefore have been constructed with an
    /// <see cref="System.IServiceProvider"/> (or resolved from a DI container,
    /// which supplies one automatically).
    /// </remarks>
    /// <typeparam name="TSource">The mapping pair's source type.</typeparam>
    /// <typeparam name="TDestination">The mapping pair's destination type.</typeparam>
    /// <example>
    /// <code>
    /// public sealed class StampAudit : IMappingAction&lt;Order, OrderDto&gt;
    /// {
    ///     private readonly IClock _clock;
    ///     public StampAudit(IClock clock) => _clock = clock;
    ///     public void Process(Order source, OrderDto destination)
    ///         => destination.MappedAtUtc = _clock.UtcNow;
    /// }
    ///
    /// config.CreateMap&lt;Order, OrderDto&gt;()
    ///       .AfterMap&lt;StampAudit&gt;();
    /// </code>
    /// </example>
    public interface IMappingAction<in TSource, in TDestination>
    {
        /// <summary>Executes the hook for one mapping operation.</summary>
        /// <param name="source">The source object being mapped.</param>
        /// <param name="destination">The destination object (constructed but not yet
        /// populated for a BeforeMap; fully populated for an AfterMap).</param>
        void Process(TSource source, TDestination destination);
    }
}
