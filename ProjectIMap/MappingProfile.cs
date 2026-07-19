namespace ProjectIMap
{
    /// <summary>
    /// Base class for defining a cohesive set of type-pair mapping rules, following
    /// the same <em>Profile</em> pattern popularised by AutoMapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derive from <see cref="MappingProfile"/> and call
    /// <see cref="CreateMap{TSource,TDestination}"/> inside your constructor to
    /// register mappings.  Profiles are discovered automatically by
    /// <see cref="Microsoft.Extensions.DependencyInjection.MapperServiceCollectionExtensions.AddMyMapper"/>
    /// when assembly-scanning is used, so no manual wiring is required.
    /// </para>
    /// <para>
    /// Each profile owns a private <see cref="MapperConfiguration"/> instance.
    /// Calls to <see cref="CreateMap{TSource,TDestination}"/> (and any chained
    /// <see cref="IMappingExpression{TSource,TDestination}.ReverseMap"/> calls) are
    /// recorded there at construction time.  When the DI extension method builds
    /// the global <see cref="MapperConfiguration"/> it calls
    /// <see cref="ApplyTo"/> on every discovered profile, which transfers all
    /// registrations in one pass — no reflection occurs in the hot path.
    /// </para>
    /// <example>
    /// <code>
    /// public class OrderProfile : MappingProfile
    /// {
    ///     public OrderProfile()
    ///     {
    ///         CreateMap&lt;Order, OrderDto&gt;().ReverseMap();
    ///         CreateMap&lt;OrderLine, OrderLineDto&gt;();
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public abstract class MappingProfile
    {
        // Each profile instance maintains its own isolated configuration so that
        // CreateMap / ReverseMap work exactly as they do on the real configuration
        // — the full fluent chain executes and is captured here at construction time.
        private readonly MapperConfiguration _innerConfig = new();

        /// <summary>
        /// Registers a mapping from <typeparamref name="TSource"/> to
        /// <typeparamref name="TDestination"/> within this profile and returns the
        /// fluent <see cref="IMappingExpression{TSource,TDestination}"/> so that
        /// <see cref="IMappingExpression{TSource,TDestination}.ReverseMap"/> (and any
        /// future options) can be chained immediately.
        /// </summary>
        /// <typeparam name="TSource">The type to map from.</typeparam>
        /// <typeparam name="TDestination">The type to map to.</typeparam>
        /// <returns>
        /// A fluent builder for this mapping direction, backed by the profile's
        /// private <see cref="MapperConfiguration"/>.
        /// </returns>
        /// <remarks>
        /// Call this method only from the derived class constructor (or from a helper
        /// method called by the constructor).  Registrations added after
        /// <see cref="ApplyTo"/> has been called will not be visible to the
        /// <see cref="Mapper"/>.
        /// </remarks>
        protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
            => _innerConfig.CreateMap<TSource, TDestination>();

        /// <summary>
        /// Transfers all type-pair registrations collected by this profile into
        /// <paramref name="configuration"/>.
        /// </summary>
        /// <param name="configuration">
        /// The global <see cref="MapperConfiguration"/> that the <see cref="Mapper"/>
        /// will use.
        /// </param>
        /// <remarks>
        /// This method is called once per profile by the assembly-scanning DI
        /// extension (<see cref="Microsoft.Extensions.DependencyInjection.MapperServiceCollectionExtensions.AddMyMapper"/>).
        /// It is <see langword="internal"/> so application code cannot invoke it
        /// directly; profiles are always applied through the DI setup.
        /// </remarks>
        internal void ApplyTo(MapperConfiguration configuration)
            => configuration.MergeFrom(_innerConfig);
    }
}
