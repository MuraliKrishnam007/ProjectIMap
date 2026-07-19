namespace ProjectIMap
{
    /// <summary>
    /// Fluent builder returned by
    /// <see cref="MapperConfiguration.CreateMap{TSource,TDestination}"/> that allows
    /// further configuration of the <typeparamref name="TSource"/> →
    /// <typeparamref name="TDestination"/> mapping rule.
    /// </summary>
    /// <typeparam name="TSource">The source type for this mapping direction.</typeparam>
    /// <typeparam name="TDestination">The destination type for this mapping direction.</typeparam>
    /// <remarks>
    /// <para>
    /// The interface follows a <b>fluent / method-chaining</b> pattern.  Each call
    /// returns a new <see cref="IMappingExpression{TSource,TDestination}"/> so that
    /// multiple configuration calls can be chained in a single expression.
    /// </para>
    /// <para>
    /// Registrations made through this interface are stored in the parent
    /// <see cref="MapperConfiguration"/> and are used by <see cref="Mapper"/> during
    /// the one-time Expression Tree compilation step.
    /// </para>
    /// </remarks>
    public interface IMappingExpression<TSource, TDestination>
    {
        /// <summary>
        /// Registers the <b>reverse</b> mapping direction
        /// (<typeparamref name="TDestination"/> → <typeparamref name="TSource"/>) in
        /// the parent <see cref="MapperConfiguration"/>, enabling bi-directional
        /// mapping between Entities and DTOs with a single fluent call.
        /// </summary>
        /// <returns>
        /// An <see cref="IMappingExpression{TSource,TDestination}"/> for the reverse
        /// direction so that further configuration (e.g. another <c>ReverseMap</c>
        /// chain) can be applied.
        /// </returns>
        /// <remarks>
        /// Calling <c>ReverseMap</c> is equivalent to calling
        /// <see cref="MapperConfiguration.CreateMap{TSource,TDestination}"/> a second
        /// time with the type arguments swapped.  The reverse mapping inherits the
        /// same convention-based property-matching rules (direct, flatten, unflatten)
        /// and is compiled independently on first use, so there is no runtime
        /// overhead until the reverse direction is actually invoked.
        /// </remarks>
        /// <example>
        /// <code>
        /// var config = new MapperConfiguration();
        /// config.CreateMap&lt;Order, OrderDto&gt;()
        ///       .ReverseMap();   // also registers OrderDto → Order
        ///
        /// IMapper mapper = new Mapper(config);
        /// var dto   = mapper.Map&lt;Order, OrderDto&gt;(order);
        /// var order = mapper.Map&lt;OrderDto, Order&gt;(dto);
        /// </code>
        /// </example>
        IMappingExpression<TDestination, TSource> ReverseMap();

        /// <summary>
        /// Configures a custom override for a single destination property, either
        /// ignoring it completely or supplying a custom source expression.
        /// </summary>
        /// <param name="destinationMember">
        /// A lambda selecting the destination property, e.g.
        /// <c>dest => dest.FullName</c>.  Value-type properties must be boxed to
        /// <see cref="object"/> by the compiler; the member name is extracted from
        /// the underlying <see cref="System.Linq.Expressions.MemberExpression"/>
        /// before the box is discarded.
        /// </param>
        /// <param name="options">
        /// A configuration callback receiving an
        /// <see cref="IMemberConfigurationExpression{TSource}"/>.  Call either
        /// <see cref="IMemberConfigurationExpression{TSource}.Ignore"/> or
        /// <see cref="IMemberConfigurationExpression{TSource}.MapFrom{TMember}"/>
        /// inside the callback.
        /// </param>
        /// <returns>
        /// The same <see cref="IMappingExpression{TSource,TDestination}"/> to allow
        /// further fluent chaining.
        /// </returns>
        /// <example>
        /// <code>
        /// config.CreateMap&lt;Employee, EmployeeDto&gt;()
        ///       .ForMember(dest => dest.FullName,
        ///                  opt => opt.MapFrom(src => src.FirstName + " " + src.LastName))
        ///       .ForMember(dest => dest.InternalCode,
        ///                  opt => opt.Ignore());
        /// </code>
        /// </example>
        IMappingExpression<TSource, TDestination> ForMember(
            System.Linq.Expressions.Expression<Func<TDestination, object>> destinationMember,
            Action<IMemberConfigurationExpression<TSource>> options);

        /// <summary>
        /// Supplies a custom construction expression for <typeparamref name="TDestination"/>,
        /// used instead of requiring a public parameterless constructor.
        /// </summary>
        /// <param name="ctor">
        /// A lambda expression that constructs the destination instance from the
        /// source, e.g. <c>s =&gt; new Dest(s.X, s.Y)</c>. The body may also include
        /// an object initializer for any remaining members, e.g.
        /// <c>s =&gt; new Dest(s.X, s.Y) { Extra = s.Z }</c> — the C# compiler already
        /// emits that as a single <c>MemberInit</c>-over-parameterized-<c>New</c> tree,
        /// so no additional merging with convention-based bindings is performed.
        /// </param>
        /// <remarks>
        /// Required for destination types with no public parameterless constructor
        /// (e.g. C# records with a primary constructor). Applies to both the
        /// runtime <see cref="Mapper"/> engine and <see cref="ProjectionCompiler"/>
        /// EF Core projections, as long as the supplied expression is itself
        /// SQL-translatable for the projection case.
        /// </remarks>
        IMappingExpression<TSource, TDestination> ConstructUsing(
            System.Linq.Expressions.Expression<Func<TSource, TDestination>> ctor);

        /// <summary>
        /// Registers a callback invoked with the source and the (already
        /// constructed, not-yet-populated) destination instance immediately
        /// before convention-based property mapping runs.
        /// </summary>
        /// <remarks>
        /// Runtime-only: has no equivalent in <see cref="ProjectionCompiler"/>'s
        /// pure EF Core projection expressions, which cannot carry side effects.
        /// Registering a hook switches <see cref="IMapper.Map{TSource,TDestination}(TSource)"/>
        /// for this pair from the zero-overhead <c>MemberInit</c> path to a
        /// construct-then-assign pipeline.
        /// </remarks>
        IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> action);

        /// <summary>
        /// Registers a callback invoked with the source and the fully-populated
        /// destination instance immediately after convention-based property
        /// mapping runs.
        /// </summary>
        /// <remarks>
        /// Runtime-only; see <see cref="BeforeMap"/> remarks.
        /// </remarks>
        IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action);

        /// <summary>
        /// Registers a class-based, DI-resolved BeforeMap hook: a fresh
        /// <typeparamref name="TAction"/> is resolved from the <see cref="Mapper"/>'s
        /// <see cref="IServiceProvider"/> on every map call and its
        /// <see cref="IMappingAction{TSource,TDestination}.Process"/> invoked.
        /// </summary>
        /// <remarks>
        /// Hooks accumulate: inline-delegate and class-based hooks may be mixed
        /// freely and all run in registration order. Requires a
        /// <c>Mapper(configuration, serviceProvider)</c>-constructed mapper (or one
        /// resolved from a DI container); mapping throws otherwise.
        /// </remarks>
        IMappingExpression<TSource, TDestination> BeforeMap<TAction>()
            where TAction : IMappingAction<TSource, TDestination>;

        /// <summary>
        /// Registers a class-based, DI-resolved AfterMap hook.
        /// See <see cref="BeforeMap{TAction}"/> for resolution semantics.
        /// </summary>
        IMappingExpression<TSource, TDestination> AfterMap<TAction>()
            where TAction : IMappingAction<TSource, TDestination>;

        /// <summary>
        /// Overrides how many times this (source, destination) pair may appear on
        /// the nested-object DFS recursion stack before being treated as a cycle.
        /// </summary>
        /// <param name="depth">
        /// Must be at least 1. The default (1) preserves the engine's original
        /// behaviour: a self-referencing pair is nulled out at the first repeat.
        /// A value of <c>n</c> allows <c>n</c> levels of real recursion (e.g. a
        /// <c>Category.Parent</c> chain) before nulling out the next level.
        /// </param>
        IMappingExpression<TSource, TDestination> MaxDepth(int depth);

        /// <summary>
        /// Overrides how many navigation-property levels deep the flatten
        /// (<c>source.Customer.Name → dest.CustomerName</c>) and unflatten
        /// (<c>source.CustomerName → dest.Customer.Name</c>) lookahead walks the
        /// source type's own property graph, for this pair.
        /// </summary>
        /// <param name="depth">
        /// Must be at least 1. The default is 5 (the engine's original hardcoded
        /// lookahead). Independent of <see cref="MaxDepth"/>, which governs
        /// self-referencing <em>object</em> recursion, not this lookahead.
        /// </param>
        IMappingExpression<TSource, TDestination> FlattenDepth(int depth);

        /// <summary>
        /// Registers <typeparamref name="TDerivedSource"/> → <typeparamref name="TDerivedDestination"/>
        /// as a polymorphic sub-mapping of this pair: when
        /// <see cref="IMapper.Map{TSource,TDestination}(TSource)"/> is called with a
        /// <typeparamref name="TSource"/>-typed reference whose runtime type is
        /// actually <typeparamref name="TDerivedSource"/>, the call dispatches to
        /// the derived pair's own mapping instead of this one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The derived pair must still be registered separately via
        /// <c>CreateMap&lt;TDerivedSource, TDerivedDestination&gt;()</c> — <c>Include</c>
        /// only records the dispatch relationship, it does not auto-register anything.
        /// If the derived pair was never registered, dispatch fails with the usual
        /// "no mapping registered" exception.
        /// </para>
        /// <para>
        /// Runtime-only (no <see cref="ProjectionCompiler"/> equivalent — EF Core's
        /// own polymorphic query support is a different mechanism), and scoped to
        /// direct top-level <c>Map(source)</c> calls: elements inside a mapped
        /// collection are not polymorphically dispatched.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// config.CreateMap&lt;Animal, AnimalDto&gt;()
        ///       .Include&lt;Dog, DogDto&gt;();
        /// config.CreateMap&lt;Dog, DogDto&gt;();   // still required
        ///
        /// Animal animal = new Dog { ... };
        /// var dto = mapper.Map&lt;Animal, AnimalDto&gt;(animal);   // returns a DogDto
        /// </code>
        /// </example>
        IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
            where TDerivedSource : TSource
            where TDerivedDestination : TDestination;

        /// <summary>
        /// Applies <paramref name="configure"/> to every writable destination
        /// member of this pair that does <b>not</b> already have an explicit
        /// <c>.ForMember(...)</c> override at the time this is called.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Call this last</b>, after every individual <c>.ForMember(...)</c> call
        /// in the same chain. <c>ForAllMembers</c> only touches a member that has no
        /// override <em>at the moment it runs</em> — it checks each member once,
        /// immediately, not lazily. Called first, it would apply to members that
        /// haven't been configured yet, and a later <c>.ForMember(...)</c> call on
        /// the same member accumulates onto that same override rather than
        /// replacing it — e.g. an earlier blanket <c>Ignore()</c> stays in effect
        /// even after a later <c>MapFrom(...)</c> on that member, since <c>Ignore</c>
        /// is checked first regardless of what else is set. Called last, as
        /// recommended, this can never happen: anything you've already configured
        /// explicitly is skipped entirely.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// config.CreateMap&lt;Order, OrderDto&gt;()
        ///       .ForMember(d =&gt; d.CalculatedTotal, opt =&gt; opt.MapFrom(s =&gt; s.Price * s.Quantity))
        ///       .ForAllMembers(opt =&gt; opt.NullSubstitute(string.Empty));
        /// </code>
        /// </example>
        IMappingExpression<TSource, TDestination> ForAllMembers(
            Action<IMemberConfigurationExpression<TSource>> configure);

        /// <summary>
        /// Registers an identity comparer for this (element) type pair, used by
        /// <see cref="IMapper.Map{TSource,TDestination}(TSource,TDestination)"/>'s
        /// collection branch to diff by identity — updating matched elements in
        /// place, removing unmatched destination elements, and adding unmatched
        /// source elements — instead of the default clear-and-rebuild merge.
        /// </summary>
        /// <param name="comparer">
        /// Returns <see langword="true"/> when a source and destination element
        /// represent the same logical entity, e.g. <c>(src, dst) =&gt; src.Id == dst.Id</c>.
        /// </param>
        /// <example>
        /// <code>
        /// config.CreateMap&lt;OrderLine, OrderLineDto&gt;()
        ///       .EqualityComparison((src, dst) =&gt; src.Id == dst.Id);
        /// // mapper.Map(order.Lines, existingDto.Lines) now updates matching lines
        /// // in place, removes lines no longer present, and adds new ones.
        /// </code>
        /// </example>
        IMappingExpression<TSource, TDestination> EqualityComparison(Func<TSource, TDestination, bool> comparer);
    }
}

