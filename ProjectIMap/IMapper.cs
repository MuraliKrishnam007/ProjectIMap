namespace ProjectIMap
{
    /// <summary>
    /// Defines a high-performance object-to-object mapper whose mapping delegates
    /// are compiled <b>once</b> to native IL via Expression Trees and cached for the
    /// lifetime of the <see cref="IMapper"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All type-pair mappings must be declared upfront through
    /// <see cref="MapperConfiguration.CreateMap{TSource,TDestination}"/> before the
    /// first call to <see cref="Map{TSource,TDestination}"/>.  Attempting to map an
    /// unregistered pair throws <see cref="System.InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// <b>Performance model</b>: the first call for a given type pair triggers a
    /// one-time Expression Tree compilation step (reflection occurs here).  Every
    /// subsequent call executes the cached, JIT-compiled delegate at near-native
    /// speed — no reflection, no boxing for value types, and no heap allocations
    /// beyond the destination object itself.
    /// </para>
    /// <para>
    /// The default implementation (<see cref="Mapper"/>) is thread-safe and is
    /// designed to be registered as a <b>Singleton</b> in a dependency-injection
    /// container.  Use
    /// <c>services.AddMyCustomMapper(cfg =&gt; cfg.CreateMap&lt;A, B&gt;())</c>
    /// for ASP.NET Core / generic-host integration.
    /// </para>
    /// </remarks>
    public interface IMapper
    {
        /// <summary>
        /// Maps <paramref name="source"/> to a new instance of
        /// <typeparamref name="TDestination"/>.
        /// </summary>
        /// <typeparam name="TSource">
        /// The source type.  Must have been registered via
        /// <see cref="MapperConfiguration.CreateMap{TSource,TDestination}"/>.
        /// </typeparam>
        /// <typeparam name="TDestination">
        /// The destination type.  Must have a public parameterless constructor.
        /// </typeparam>
        /// <param name="source">The object to map from.  Must not be <see langword="null"/>.</param>
        /// <returns>A new <typeparamref name="TDestination"/> populated by the compiled mapping delegate.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="source"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when no mapping has been registered for the
        /// <typeparamref name="TSource"/> → <typeparamref name="TDestination"/> pair.
        /// </exception>
        TDestination Map<TSource, TDestination>(TSource source);

        /// <summary>
        /// Maps <paramref name="source"/> to a new <typeparamref name="TDestination"/>,
        /// inferring the source type from the object's <b>runtime</b> type — the
        /// boilerplate-free form of <see cref="Map{TSource,TDestination}(TSource)"/>:
        /// <c>mapper.Map&lt;OrderDto&gt;(order)</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The runtime type is resolved to a registered pair once per
        /// (runtime type, destination) combination and the dispatch is compiled and
        /// cached, so steady-state cost is identical to the explicit two-generic
        /// overload: one cache lookup, then the same compiled delegate.
        /// </para>
        /// <para>
        /// When the runtime type itself has no registered pair, its base types are
        /// walked so a derived instance registered only via its base pair (typically
        /// with <see cref="IMappingExpression{TSource,TDestination}.Include{TDerivedSource,TDerivedDestination}"/>)
        /// still dispatches correctly. Collections infer their element pair exactly
        /// like the explicit overload.
        /// </para>
        /// </remarks>
        /// <typeparam name="TDestination">The destination type.</typeparam>
        /// <param name="source">The object to map from. Must not be <see langword="null"/> —
        /// a null carries no runtime type to infer from.</param>
        /// <returns>A new <typeparamref name="TDestination"/> populated by the compiled mapping delegate.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="source"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when no mapping is registered from the runtime type (or any of its
        /// base types) to <typeparamref name="TDestination"/>.
        /// </exception>
        TDestination Map<TDestination>(object source);

        /// <summary>
        /// Maps <paramref name="source"/> onto the already-constructed
        /// <paramref name="destination"/> instance in place, instead of allocating
        /// a new <typeparamref name="TDestination"/>.
        /// </summary>
        /// <remarks>
        /// Useful for updating an existing, possibly EF-tracked, entity from a DTO.
        /// Combine with <see cref="IMemberConfigurationExpression{TSource}.Condition"/>
        /// to leave selected destination properties untouched when a predicate is
        /// false, rather than overwriting them with the source's value.
        /// Nested complex properties are still freshly constructed; existing nested
        /// objects on <paramref name="destination"/> are replaced, not merged into.
        /// </remarks>
        /// <param name="source">The object to map from. Must not be <see langword="null"/>.</param>
        /// <param name="destination">The object to map onto. Must not be <see langword="null"/>.</param>
        /// <returns><paramref name="destination"/>, after in-place mapping.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when no mapping has been registered for the requested type pair.
        /// </exception>
        TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
    }
}

