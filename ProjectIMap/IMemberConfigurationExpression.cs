using System;
using System.Linq.Expressions;

namespace ProjectIMap
{
    /// <summary>
    /// Provides per-member configuration options used inside a
    /// <see cref="IMappingExpression{TSource,TDestination}.ForMember"/> call.
    /// </summary>
    /// <typeparam name="TSource">The source type of the enclosing mapping.</typeparam>
    /// <remarks>
    /// Obtain an instance of this interface exclusively through
    /// <see cref="IMappingExpression{TSource,TDestination}.ForMember"/>.
    /// Both options are mutually exclusive: calling <see cref="Ignore"/> after
    /// <see cref="MapFrom{TMember}"/> (or vice-versa) on the same member
    /// replaces the previous instruction.
    /// </remarks>
    public interface IMemberConfigurationExpression<TSource>
    {
        /// <summary>
        /// Instructs the Expression Tree compiler to skip this destination property
        /// entirely.  No binding is emitted and convention-based matching is
        /// suppressed for this member.
        /// </summary>
        /// <remarks>
        /// Use <c>Ignore</c> for computed, read-only, or security-sensitive
        /// destination properties that must never be populated automatically.
        /// </remarks>
        void Ignore();

        /// <summary>
        /// Provides a custom <see cref="Expression"/> to compute the value for the
        /// destination property instead of relying on convention-based matching.
        /// </summary>
        /// <typeparam name="TMember">The type produced by the custom expression.</typeparam>
        /// <param name="sourceMember">
        /// A lambda expression whose body is inlined directly into the compiled
        /// mapping delegate.  The expression is substituted at compile time — it is
        /// not invoked via a delegate at map-call time — so it carries <b>zero
        /// per-call overhead</b> beyond the native IL instructions it produces.
        /// </param>
        /// <example>
        /// <code>
        /// // Simple property remap
        /// CreateMap&lt;Employee, EmployeeDto&gt;()
        ///     .ForMember(dest => dest.FullName,
        ///                opt => opt.MapFrom(src => src.FirstName + " " + src.LastName));
        ///
        /// // Nested path
        /// CreateMap&lt;Order, OrderDto&gt;()
        ///     .ForMember(dest => dest.City,
        ///                opt => opt.MapFrom(src => src.Address.City));
        /// </code>
        /// </example>
        void MapFrom<TMember>(Expression<Func<TSource, TMember>> sourceMember);

        /// <summary>
        /// Provides a reusable <see cref="IValueResolver{TSource,TMember}"/>
        /// instance to compute the value for the destination property, as an
        /// alternative to an inline <see cref="MapFrom{TMember}"/> lambda.
        /// </summary>
        /// <param name="resolver">
        /// An already-constructed resolver instance. <typeparamref name="TMember"/>
        /// is inferred from this argument's implemented
        /// <see cref="IValueResolver{TSource,TMember}"/>, so callers just write
        /// <c>opt.MapFrom(new FullNameResolver())</c>. Construct the instance
        /// yourself (optionally passing dependencies to its constructor) — this
        /// library does not wire dependency injection into resolver construction.
        /// </param>
        /// <remarks>
        /// <para>
        /// The resolver instance is captured once, at configuration time, as a
        /// compiled-in constant; <c>Resolve</c> becomes a single virtual call
        /// inside the compiled mapping delegate — no reflection or delegate
        /// invocation overhead beyond that one call. It reuses the same storage
        /// slot as <see cref="MapFrom{TMember}"/>, so it composes with
        /// <see cref="Condition"/> and <see cref="NullSubstitute{TMember}"/> exactly
        /// like an inline lambda would.
        /// </para>
        /// <para>
        /// <b>Not EF-translatable:</b> unlike an inline <c>MapFrom</c> lambda that
        /// decomposes into a pure property-access chain, a resolver's <c>Resolve</c>
        /// call is an opaque method call. It works for the runtime
        /// <see cref="Mapper"/> engine but is not recommended for
        /// <see cref="ProjectionCompiler"/>/<c>ProjectTo</c> — EF Core cannot
        /// translate it to SQL and will either throw or fall back to client evaluation.
        /// </para>
        /// </remarks>
        void MapFrom<TMember>(IValueResolver<TSource, TMember> resolver);

        /// <summary>
        /// Resolves a fresh <typeparamref name="TResolver"/> instance from the
        /// <see cref="Mapper"/>'s <see cref="IServiceProvider"/> on every map call,
        /// instead of capturing one constructed instance at configuration time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Requires a <see cref="Mapper"/> constructed via
        /// <c>new Mapper(configuration, serviceProvider)</c> (or resolved through a
        /// DI container, which supplies <see cref="IServiceProvider"/> automatically);
        /// otherwise this throws at map time. Use this over the instance-taking
        /// <see cref="MapFrom{TMember}(IValueResolver{TSource,TMember})"/> overload
        /// when the resolver depends on a scoped or transient service (e.g. a
        /// per-request <c>DbContext</c>) that must not be captured once and reused.
        /// </para>
        /// <para>
        /// Both type parameters must be given explicitly — there is no argument to
        /// infer them from: <c>opt.MapFrom&lt;MyResolver, string&gt;()</c>.
        /// Not EF-translatable, same as the instance-taking overload.
        /// </para>
        /// </remarks>
        void MapFrom<TResolver, TMember>() where TResolver : IValueResolver<TSource, TMember>;

        /// <summary>
        /// Only writes the destination member when <paramref name="condition"/>
        /// evaluates to <see langword="true"/> for the source object.
        /// </summary>
        /// <remarks>
        /// When mapping into a freshly-constructed destination (<see cref="IMapper.Map{TSource,TDestination}(TSource)"/>),
        /// a <see langword="false"/> condition leaves the member at its CLR default
        /// — there is nothing else to leave it as. When mapping into an existing
        /// instance (<see cref="IMapper.Map{TSource,TDestination}(TSource,TDestination)"/>),
        /// a <see langword="false"/> condition truly skips the assignment, leaving
        /// the existing destination value untouched.
        /// </remarks>
        void Condition(Expression<Func<TSource, bool>> condition);

        /// <summary>
        /// Substitutes <paramref name="substitute"/> for the resolved source value
        /// when that value is <see langword="null"/>, instead of propagating null
        /// or throwing <see cref="MappingNullInvariantException"/>.
        /// </summary>
        /// <remarks>
        /// Applies to the value produced by a same-callback <see cref="MapFrom{TMember}"/>
        /// call if present, otherwise to the convention-matched source property of
        /// the same name.
        /// </remarks>
        void NullSubstitute<TMember>(TMember substitute);
    }
}
