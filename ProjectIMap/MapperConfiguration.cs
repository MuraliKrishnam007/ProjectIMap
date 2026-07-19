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

        // Lifecycle hooks, keyed by (SourceType, DestType). Stored as boxed Action<TSource,TDestination>.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _beforeMap = new();
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _afterMap  = new();

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

        internal void SetBeforeMap(Type source, Type destination, Delegate hook)
            => _beforeMap[(source, destination)] = hook;

        internal void SetAfterMap(Type source, Type destination, Delegate hook)
            => _afterMap[(source, destination)] = hook;

        internal bool TryGetBeforeMap(Type source, Type destination, out Delegate? hook)
            => _beforeMap.TryGetValue((source, destination), out hook);

        internal bool TryGetAfterMap(Type source, Type destination, out Delegate? hook)
            => _afterMap.TryGetValue((source, destination), out hook);

        internal bool HasLifecycleHooksOrCustomConstructor(Type source, Type destination)
            => _beforeMap.ContainsKey((source, destination))
            || _afterMap.ContainsKey((source, destination))
            || _customConstructors.ContainsKey((source, destination));

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

            foreach (var (pair, hook) in other._beforeMap)
                _beforeMap[pair] = hook;

            foreach (var (pair, hook) in other._afterMap)
                _afterMap[pair] = hook;

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
