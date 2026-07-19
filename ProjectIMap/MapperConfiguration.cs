using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ProjectIMap
{
    /// <summary>
    /// Central registry of permitted type-pair mappings.
    /// Compilation of Expression Tree delegates is deferred to <see cref="Mapper"/>.
    /// </summary>
    public sealed class MapperConfiguration
    {
        // Acts as a thread-safe set; the bool value is unused.
        private readonly ConcurrentDictionary<(Type, Type), bool> _registeredPairs = new();

        // Outer key: (SourceType, DestType). Inner key: destination property name (ordinal).
        private readonly ConcurrentDictionary<(Type, Type), ConcurrentDictionary<string, MemberOverride>>
            _memberOverrides = new();

        // Custom construction expressions, keyed by (SourceType, DestType). When present,
        // this replaces the default `new TDestination()` (or requirement thereof) during compilation.
        private readonly ConcurrentDictionary<(Type, Type), LambdaExpression> _customConstructors = new();

        // Lifecycle hooks, keyed by (SourceType, DestType). Each entry is either a
        // boxed Action<TSource,TDestination> or a Type implementing
        // IMappingAction<TSource,TDestination> (resolved from the Mapper's
        // IServiceProvider on every call). Hooks accumulate and run in registration
        // order — a second registration adds, it never replaces. Mutated only at
        // configuration time, so a simple lock on each list suffices.
        private readonly ConcurrentDictionary<(Type, Type), List<object>> _beforeMap = new();
        private readonly ConcurrentDictionary<(Type, Type), List<object>> _afterMap  = new();

        // Global type converters, keyed by (SourceType, DestType) of the VALUE being
        // adapted (not the mapped pair). Applied by both compilers with precedence
        // over every built-in adaptation rule, wherever a member's value adapts.
        private readonly ConcurrentDictionary<(Type, Type), LambdaExpression> _typeConverters = new();

        // Per-pair override for the DFS self-reference recursion depth (see Mapper's
        // visitedPath guard). Default of 1 means "a pair may appear once on the DFS
        // stack; the first repeat is treated as a cycle" — today's exact behaviour.
        internal const int DefaultMaxDepth = 1;
        private readonly ConcurrentDictionary<(Type, Type), int> _maxDepth = new();

        // Per-pair override for how many navigation-property levels deep the
        // flatten/unflatten trie looks ahead (see Mapper/ProjectionCompiler's
        // PopulateTrie). Independent of MaxDepth: this bounds a lookahead over the
        // SOURCE type's own property graph, not recursion over a (source,dest) pair.
        internal const int DefaultFlattenDepth = 5;
        private readonly ConcurrentDictionary<(Type, Type), int> _flattenDepth = new();

        // Polymorphic dispatch: for a base (SourceType, DestType) pair, the set of
        // (DerivedSourceType, DerivedDestType) pairs registered via Include<,>.
        // Mutated only at registration time (CreateMap/Include calls), so a simple
        // lock on the list is fine — this never runs on the Map<>() hot path.
        private readonly ConcurrentDictionary<(Type, Type), List<(Type, Type)>> _includedTypes = new();

        // Identity comparers for collection-merge diffing, keyed by the ELEMENT
        // (SourceType, DestType) pair. Stored as boxed Func<TSrcElem,TDstElem,bool>.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _equalityComparers = new();

        /// <summary>
        /// Registers a mapping from <typeparamref name="TSource"/> to
        /// <typeparamref name="TDestination"/> and returns a fluent expression
        /// that allows calling <see cref="IMappingExpression{TSource,TDestination}.ReverseMap"/>.
        /// </summary>
        public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
        {
            RegisterMap<TSource, TDestination>();
            return new MappingExpression<TSource, TDestination>(this);
        }

        internal void RegisterMap<TSource, TDestination>()
            => _registeredPairs[(typeof(TSource), typeof(TDestination))] = true;

        /// <summary>
        /// Non-generic variant used by <see cref="MappingProfile.ApplyTo"/> to merge
        /// profile registrations without requiring generic type parameters at the
        /// call site.
        /// </summary>
        internal void RegisterPair(Type source, Type destination)
            => _registeredPairs[(source, destination)] = true;

        /// <summary>
        /// Returns a snapshot of all registered type pairs.
        /// Used by <see cref="MappingProfile.ApplyTo"/> to transfer registrations
        /// from a profile's private inner configuration to the global one.
        /// </summary>
        internal IEnumerable<(Type Source, Type Destination)> GetRegisteredPairs()
            => _registeredPairs.Keys;

        /// <summary>
        /// Returns <see langword="true"/> when a mapping pair has been explicitly registered.
        /// </summary>
        internal bool IsRegistered(Type source, Type destination)
            => _registeredPairs.ContainsKey((source, destination));

        /// <summary>
        /// Returns the <see cref="MemberOverride"/> registered for
        /// <paramref name="destPropertyName"/> on the given type pair, creating an
        /// empty one on first access. Multiple option calls in the same
        /// <c>.ForMember(...)</c> callback (e.g. <c>MapFrom</c> then <c>Condition</c>)
        /// accumulate onto the same mutable instance rather than replacing it.
        /// </summary>
        internal MemberOverride GetOrCreateMemberOverride(Type source, Type destination, string destPropertyName)
        {
            var overrides = _memberOverrides.GetOrAdd(
                (source, destination),
                static _ => new ConcurrentDictionary<string, MemberOverride>(StringComparer.Ordinal));
            return overrides.GetOrAdd(destPropertyName, static _ => new MemberOverride());
        }

        /// <summary>
        /// Attempts to retrieve the <see cref="MemberOverride"/> registered for
        /// <paramref name="destPropertyName"/> on the given type pair.
        /// </summary>
        internal bool TryGetMemberOverride(
            Type              source,
            Type              destination,
            string            destPropertyName,
            out MemberOverride? @override)
        {
            if (_memberOverrides.TryGetValue((source, destination), out var overrides)
                && overrides.TryGetValue(destPropertyName, out var found))
            {
                @override = found;
                return true;
            }

            @override = null;
            return false;
        }

        // ── Custom construction (ConstructUsing) ────────────────────────────────

        /// <summary>
        /// Registers a custom construction expression for a type pair, replacing
        /// the default parameterless-constructor requirement during compilation.
        /// </summary>
        internal void SetCustomConstructor(Type source, Type destination, LambdaExpression ctor)
            => _customConstructors[(source, destination)] = ctor;

        /// <summary>
        /// Attempts to retrieve the custom construction expression registered for
        /// a type pair via <see cref="IMappingExpression{TSource,TDestination}.ConstructUsing"/>.
        /// </summary>
        internal bool TryGetCustomConstructor(Type source, Type destination, out LambdaExpression? ctor)
            => _customConstructors.TryGetValue((source, destination), out ctor);

        // ── Lifecycle hooks (BeforeMap / AfterMap) ──────────────────────────────

        internal void AddBeforeMap(Type source, Type destination, object hookEntry)
        {
            var list = _beforeMap.GetOrAdd((source, destination), static _ => []);
            lock (list) list.Add(hookEntry);
        }

        internal void AddAfterMap(Type source, Type destination, object hookEntry)
        {
            var list = _afterMap.GetOrAdd((source, destination), static _ => []);
            lock (list) list.Add(hookEntry);
        }

        /// <summary>
        /// Snapshot of the registered BeforeMap entries for a pair, in registration
        /// order. Each entry is either a boxed <c>Action&lt;TSource,TDestination&gt;</c>
        /// or a <see cref="Type"/> implementing <c>IMappingAction&lt;TSource,TDestination&gt;</c>.
        /// Empty array when none are registered.
        /// </summary>
        internal object[] GetBeforeMaps(Type source, Type destination)
        {
            if (!_beforeMap.TryGetValue((source, destination), out var list)) return [];
            lock (list) return list.ToArray();
        }

        /// <inheritdoc cref="GetBeforeMaps"/>
        internal object[] GetAfterMaps(Type source, Type destination)
        {
            if (!_afterMap.TryGetValue((source, destination), out var list)) return [];
            lock (list) return list.ToArray();
        }

        internal bool HasLifecycleHooksOrCustomConstructor(Type source, Type destination)
            => _beforeMap.ContainsKey((source, destination))
            || _afterMap.ContainsKey((source, destination))
            || _customConstructors.ContainsKey((source, destination));

        // ── Global type converters (ConvertUsing) ───────────────────────────────

        /// <summary>
        /// Registers a global type converter applied wherever a value of
        /// <typeparamref name="TValueSource"/> must become a
        /// <typeparamref name="TValueDestination"/> during member adaptation —
        /// across <b>every</b> registered pair, in both the runtime engine and
        /// <c>ProjectTo</c> projections (the lambda is inlined, so it stays
        /// SQL-translatable if its body is).
        /// </summary>
        /// <remarks>
        /// Converters take precedence over every built-in adaptation rule,
        /// including direct assignability — registering
        /// <c>ConvertUsing&lt;string, string&gt;(s =&gt; s.Trim())</c> transforms every
        /// string-to-string member in the configuration. Converters never observe
        /// <see langword="null"/>: a null source value propagates as
        /// <c>default(TValueDestination)</c> without invoking the lambda, so
        /// converters like <c>s =&gt; s.Trim()</c> are inherently null-safe. Explicit per-member
        /// <c>ForMember(... MapFrom ...)</c> overrides are unaffected in what they
        /// produce, but their <em>result</em> value is also converter-adapted when
        /// its type requires conversion to the destination member's type.
        /// </remarks>
        /// <example>
        /// <code>
        /// config.ConvertUsing&lt;string, Guid&gt;(s =&gt; Guid.Parse(s));
        /// config.ConvertUsing&lt;DateTime, string&gt;(d =&gt; d.ToString("O"));
        /// </code>
        /// </example>
        public MapperConfiguration ConvertUsing<TValueSource, TValueDestination>(
            System.Linq.Expressions.Expression<Func<TValueSource, TValueDestination>> conversion)
        {
            ArgumentNullException.ThrowIfNull(conversion);
            _typeConverters[(typeof(TValueSource), typeof(TValueDestination))] = conversion;
            return this;
        }

        internal bool TryGetTypeConverter(
            Type source, Type destination,
            out System.Linq.Expressions.LambdaExpression? converter)
            => _typeConverters.TryGetValue((source, destination), out converter);

        // ── Recursion depth (MaxDepth) ──────────────────────────────────────────

        /// <summary>
        /// Sets the maximum number of times a (source, destination) pair may appear
        /// on the DFS nested-object recursion stack before being treated as a cycle.
        /// </summary>
        internal void SetMaxDepth(Type source, Type destination, int depth)
            => _maxDepth[(source, destination)] = depth;

        /// <summary>
        /// Returns the configured max depth for a pair, or <see cref="DefaultMaxDepth"/>
        /// (today's behaviour: a pair may appear once before being treated as a cycle).
        /// </summary>
        internal int GetMaxDepth(Type source, Type destination)
            => _maxDepth.TryGetValue((source, destination), out var depth) ? depth : DefaultMaxDepth;

        // ── Navigation lookahead depth (FlattenDepth) ───────────────────────────

        /// <summary>
        /// Sets how many navigation-property levels deep the flatten/unflatten
        /// trie looks ahead for a pair, overriding <see cref="DefaultFlattenDepth"/>.
        /// </summary>
        internal void SetFlattenDepth(Type source, Type destination, int depth)
            => _flattenDepth[(source, destination)] = depth;

        /// <summary>
        /// Returns the configured flatten depth for a pair, or
        /// <see cref="DefaultFlattenDepth"/> when unconfigured.
        /// </summary>
        internal int GetFlattenDepth(Type source, Type destination)
            => _flattenDepth.TryGetValue((source, destination), out var depth) ? depth : DefaultFlattenDepth;

        // ── Polymorphic dispatch (Include) ──────────────────────────────────────

        /// <summary>
        /// Records that instances of <paramref name="derivedSource"/> (a subtype of
        /// the base pair's source) should dispatch to the
        /// <paramref name="derivedSource"/> → <paramref name="derivedDestination"/>
        /// mapping instead of the base pair's own mapping. The derived pair must
        /// still be registered separately via <c>CreateMap</c>.
        /// </summary>
        internal void AddInclude(Type baseSource, Type baseDestination, Type derivedSource, Type derivedDestination)
        {
            var list = _includedTypes.GetOrAdd((baseSource, baseDestination), static _ => []);
            lock (list)
                list.Add((derivedSource, derivedDestination));
        }

        /// <summary>
        /// Returns a snapshot of the derived (source, destination) pairs registered
        /// via <see cref="IMappingExpression{TSource,TDestination}.Include{TDerivedSource,TDerivedDestination}"/>
        /// for a base pair, or an empty array when none were registered.
        /// </summary>
        internal (Type DerivedSource, Type DerivedDestination)[] GetIncludedTypes(Type baseSource, Type baseDestination)
        {
            if (!_includedTypes.TryGetValue((baseSource, baseDestination), out var list))
                return [];

            lock (list)
                return list.ToArray();
        }

        // ── Identity comparers (collection merge diffing) ───────────────────────

        /// <summary>
        /// Registers an equality comparer for an element type pair, used by
        /// <see cref="Mapper.Map{TSource,TDestination}(TSource,TDestination)"/>'s
        /// collection branch to diff by identity (add/update/remove) instead of
        /// clearing and rebuilding the destination collection wholesale.
        /// </summary>
        internal void SetEqualityComparer(Type source, Type destination, Delegate comparer)
            => _equalityComparers[(source, destination)] = comparer;

        /// <summary>
        /// Attempts to retrieve the equality comparer registered for an element
        /// type pair via <see cref="IMappingExpression{TSource,TDestination}.EqualityComparison"/>.
        /// </summary>
        internal bool TryGetEqualityComparer(Type source, Type destination, out Delegate? comparer)
            => _equalityComparers.TryGetValue((source, destination), out comparer);

        /// <summary>
        /// Merges all registered pairs, member overrides, custom constructors,
        /// lifecycle hooks, max-depth overrides, flatten-depth overrides,
        /// polymorphic includes, and equality comparers from <paramref name="other"/>
        /// into this instance. Used by <see cref="MappingProfile.ApplyTo"/> to
        /// transfer profile registrations to the global configuration.
        /// </summary>
        internal void MergeFrom(MapperConfiguration other)
        {
            foreach (var pair in other._registeredPairs.Keys)
                _registeredPairs[pair] = true;

            foreach (var (pair, overrides) in other._memberOverrides)
            {
                var dest = _memberOverrides.GetOrAdd(
                    pair,
                    static _ => new ConcurrentDictionary<string, MemberOverride>(StringComparer.Ordinal));
                foreach (var (propName, memberOverride) in overrides)
                    dest[propName] = memberOverride;
            }

            foreach (var (pair, ctor) in other._customConstructors)
                _customConstructors[pair] = ctor;

            foreach (var (pair, otherHooks) in other._beforeMap)
            {
                var list = _beforeMap.GetOrAdd(pair, static _ => []);
                lock (list)
                {
                    lock (otherHooks)
                        list.AddRange(otherHooks);
                }
            }

            foreach (var (pair, otherHooks) in other._afterMap)
            {
                var list = _afterMap.GetOrAdd(pair, static _ => []);
                lock (list)
                {
                    lock (otherHooks)
                        list.AddRange(otherHooks);
                }
            }

            foreach (var (pair, converter) in other._typeConverters)
                _typeConverters[pair] = converter;

            foreach (var (pair, depth) in other._maxDepth)
                _maxDepth[pair] = depth;

            foreach (var (pair, depth) in other._flattenDepth)
                _flattenDepth[pair] = depth;

            foreach (var (pair, otherList) in other._includedTypes)
            {
                var list = _includedTypes.GetOrAdd(pair, static _ => []);
                lock (list)
                {
                    lock (otherList)
                        list.AddRange(otherList);
                }
            }

            foreach (var (pair, comparer) in other._equalityComparers)
                _equalityComparers[pair] = comparer;
        }

        // ── Configuration validation ─────────────────────────────────────────────

        /// <summary>
        /// Validates every registered type pair and throws a single
        /// <see cref="MapperConfigurationException"/> listing every problem found,
        /// instead of letting the first bad mapping fail lazily on its first
        /// <see cref="Mapper.Map{TSource,TDestination}(TSource)"/> call.
        /// </summary>
        /// <remarks>
        /// Checks performed per pair, recursing into nested complex-type property
        /// pairs (e.g. <c>Order.Customer → OrderDto.Customer</c>, guarded against
        /// self-referencing types such as <c>Category.Parent</c>):
        /// <list type="bullet">
        ///   <item>The destination type has a public parameterless constructor,
        ///   or a custom constructor was registered via <c>ConstructUsing</c>.</item>
        ///   <item>Every writable destination property is either matched by
        ///   convention (direct name with an actually-adaptable type, flatten,
        ///   unflatten, or a valid nested-object mapping) or explicitly configured
        ///   via <c>ForMember</c> (<c>Ignore</c> or <c>MapFrom</c>).</item>
        /// </list>
        /// </remarks>
        /// <exception cref="MapperConfigurationException">
        /// Thrown when one or more registered pairs fail validation.
        /// </exception>
        public void AssertConfigurationIsValid()
        {
            var errors = new List<string>();

            foreach (var (source, destination) in _registeredPairs.Keys)
                errors.AddRange(Mapper.ValidatePair(source, destination, this));

            if (errors.Count > 0)
                throw new MapperConfigurationException(
                    "ProjectIMap configuration is invalid:" + Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
        }
    }
}
