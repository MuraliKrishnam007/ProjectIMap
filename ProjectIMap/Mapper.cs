using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ProjectIMap
{
    /// <summary>
    /// Executes object-to-object mappings via Expression Tree delegates that are
    /// compiled once to native IL and cached for all subsequent calls.
    /// </summary>
    public sealed class Mapper : IMapper
    {
        private readonly MapperConfiguration _configuration;
        private readonly IServiceProvider?   _serviceProvider;

        // Compiled delegates are cached per type-pair. GetOrAdd guarantees that
        // CompileMapping is called at most once per pair even under concurrency.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _compiledMaps = new();

        // Action<TSource,TDestination> delegates that assign onto an existing
        // destination instance instead of constructing a new one. Backs both the
        // public Map(source, destination) overload and the BeforeMap/AfterMap
        // hook-aware dispatch path.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _compiledAssign = new();

        // Func<TSource,TDestination> "blank construction" delegates: either the
        // registered ConstructUsing lambda, or a bare `new TDestination()` that
        // ignores the source. Used only by the hook-aware dispatch path.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _compiledConstructors = new();

        // Func<object,object> dispatchers for Include<> polymorphic mapping, keyed by
        // the DERIVED (source, destination) pair. Built once via reflection (same
        // pattern as CompileElementMapper) then invoked with zero further reflection.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _polymorphicDispatch = new();

        // Polymorphism-aware element/nested mappers, keyed by the element (or nested)
        // (source, destination) pair. Deliberately separate from _compiledMaps: that
        // cache's base-pair slot must stay the plain base MemberInit delegate (the
        // top-level Map path relies on it), whereas this slot holds a runtime-
        // dispatching wrapper whenever the pair has Include<> registrations, so a
        // List<Animal> element or a nested Animal member that is really a Dog maps to
        // DogDto. Pairs without Include<> reuse the very same plain base delegate here,
        // so non-polymorphic collections/nested members keep their zero-overhead path.
        private readonly ConcurrentDictionary<(Type, Type), Delegate> _elementMappers = new();

        // Trie cache is static: one trie per (source type, flatten depth), shared
        // across all Mapper instances. Depth is part of the key because different
        // pairs sharing a source type may configure different FlattenDepth values.
        private static readonly ConcurrentDictionary<(Type, int), PropertyTrieNode> _trieCache = new();

        public Mapper(MapperConfiguration configuration)
        {
            _configuration = configuration
                ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <summary>
        /// Creates a <see cref="Mapper"/> that can resolve DI-registered
        /// <see cref="IValueResolver{TSource,TMember}"/> types configured via the
        /// type-only <c>MapFrom&lt;TResolver,TMember&gt;()</c> overload, resolving a
        /// fresh instance from <paramref name="serviceProvider"/> on every map call
        /// (correct for scoped/transient dependencies).
        /// </summary>
        /// <remarks>
        /// When registered via <c>AddMyCustomMapper</c>/<c>AddMyMapper</c> and
        /// resolved through a DI container, this constructor is chosen
        /// automatically — <see cref="IServiceProvider"/> is always resolvable,
        /// so the container prefers this two-parameter constructor over the
        /// single-parameter one.
        /// </remarks>
        public Mapper(MapperConfiguration configuration, IServiceProvider serviceProvider)
        {
            _configuration = configuration
                ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider
                ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        // ── IMapper ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no mapping has been registered for the requested type pair.
        /// </exception>
        public TDestination Map<TSource, TDestination>(TSource source)
        {
            if (source is null)
                throw new ArgumentNullException(nameof(source));

            var sourceType = typeof(TSource);
            var destType   = typeof(TDestination);

            // ── Collection mapping path ──────────────────────────────────────────
            // When both sides are IEnumerable<T> (arrays, List<T>, etc.) we compile
            // a Select projection over the element-level mapper instead of a member-
            // init expression.  The element pair must still be registered explicitly.
            if (TryGetCollectionElementType(sourceType, out var srcElemType) &&
                TryGetCollectionElementType(destType,   out var dstElemType))
            {
                if (!_configuration.IsRegistered(srcElemType!, dstElemType!))
                    throw new InvalidOperationException(
                        $"No mapping registered from '{srcElemType!.Name}' to '{dstElemType!.Name}'. " +
                        $"Collection mapping requires the element type pair to be registered. " +
                        $"Call CreateMap<{srcElemType.Name}, {dstElemType.Name}>() in your MapperConfiguration.");

                var compiled = _compiledMaps.GetOrAdd(
                    (sourceType, destType),
                    _ => CompileCollectionMapping<TSource, TDestination>(srcElemType!, dstElemType!, this));

                return ((Func<TSource, TDestination>)compiled)(source);
            }

            // ── Object-to-object mapping path ────────────────────────────────────
            if (!_configuration.IsRegistered(sourceType, destType))
                throw new InvalidOperationException(
                    $"No mapping registered from '{sourceType.Name}' to '{destType.Name}'. " +
                    $"Call CreateMap<{sourceType.Name}, {destType.Name}>() in your MapperConfiguration.");

            // ── Polymorphic dispatch (Include<>) ─────────────────────────────────
            // Only meaningful when the runtime type is actually a subtype — a plain
            // TSource instance falls straight through to the normal compiled path.
            var runtimeType = source.GetType();
            if (runtimeType != sourceType)
            {
                var included = _configuration.GetIncludedTypes(sourceType, destType);
                foreach (var (derivedSrc, derivedDst) in included)
                {
                    if (!derivedSrc.IsInstanceOfType(source)) continue;

                    var dispatch = (Func<object, object>)_polymorphicDispatch.GetOrAdd(
                        (derivedSrc, derivedDst), _ => CompilePolymorphicDispatch(derivedSrc, derivedDst));

                    return (TDestination)dispatch(source);
                }
            }

            // Pairs with a custom constructor and/or BeforeMap/AfterMap hooks take
            // a slightly slower construct-then-assign path; everything else keeps
            // the original zero-overhead MemberInit-Func path unchanged.
            if (_configuration.HasLifecycleHooksOrCustomConstructor(sourceType, destType))
                return MapWithHooks<TSource, TDestination>(source, sourceType, destType);

            var cfg2 = _configuration;
            var sp2  = _serviceProvider;
            var compiled2 = _compiledMaps.GetOrAdd(
                (sourceType, destType),
                _ => CompileMapping<TSource, TDestination>(cfg2, sp2, this));

            return ((Func<TSource, TDestination>)compiled2)(source);
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no mapping has been registered for the requested (element)
        /// type pair, or when the destination collection type cannot be cleared
        /// and repopulated (e.g. an array).
        /// </exception>
        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
        {
            if (source is null)
                throw new ArgumentNullException(nameof(source));
            if (destination is null)
                throw new ArgumentNullException(nameof(destination));

            var sourceType = typeof(TSource);
            var destType   = typeof(TDestination);

            // ── Collection merge path ────────────────────────────────────────────
            // Clears destination and repopulates it via the element mapper — the
            // same "replace contents" default AutoMapper applies when no identity/
            // equality comparer is configured. Not an add/update/remove diff.
            if (TryGetCollectionElementType(sourceType, out var srcElemType) &&
                TryGetCollectionElementType(destType,   out var dstElemType))
            {
                if (!_configuration.IsRegistered(srcElemType!, dstElemType!))
                    throw new InvalidOperationException(
                        $"No mapping registered from '{srcElemType!.Name}' to '{dstElemType!.Name}'. " +
                        $"Collection mapping requires the element type pair to be registered. " +
                        $"Call CreateMap<{srcElemType.Name}, {dstElemType.Name}>() in your MapperConfiguration.");

                var mergeAction = (Action<TSource, TDestination>)_compiledAssign.GetOrAdd(
                    (sourceType, destType),
                    _ => CompileCollectionMerge<TSource, TDestination>(srcElemType!, dstElemType!, this));

                mergeAction(source, destination);
                return destination;
            }

            if (!_configuration.IsRegistered(sourceType, destType))
                throw new InvalidOperationException(
                    $"No mapping registered from '{sourceType.Name}' to '{destType.Name}'. " +
                    $"Call CreateMap<{sourceType.Name}, {destType.Name}>() in your MapperConfiguration.");

            if (_configuration.TryGetBeforeMap(sourceType, destType, out var before))
                ((Action<TSource, TDestination>)before!)(source, destination);

            var assignAction = (Action<TSource, TDestination>)_compiledAssign.GetOrAdd(
                (sourceType, destType), _ => CompileAssignment<TSource, TDestination>(_configuration, _serviceProvider, this));
            assignAction(source, destination);

            if (_configuration.TryGetAfterMap(sourceType, destType, out var after))
                ((Action<TSource, TDestination>)after!)(source, destination);

            return destination;
        }

        /// <summary>
        /// Construct-then-assign pipeline used for pairs with a custom constructor
        /// and/or BeforeMap/AfterMap hooks registered. See <see cref="IMappingExpression{TSource,TDestination}.ConstructUsing"/>,
        /// <see cref="IMappingExpression{TSource,TDestination}.BeforeMap"/>, and
        /// <see cref="IMappingExpression{TSource,TDestination}.AfterMap"/>.
        /// </summary>
        /// <remarks>
        /// When a custom constructor is registered, convention-based property
        /// assignment is skipped entirely — the constructor expression is treated
        /// as authoritative, mirroring <see cref="CompileMapping{TSource,TDestination}"/>'s
        /// behaviour for the same pair.
        /// </remarks>
        private TDestination MapWithHooks<TSource, TDestination>(
            TSource source, Type sourceType, Type destType)
        {
            var hasCustomCtor = _configuration.TryGetCustomConstructor(sourceType, destType, out _);

            var constructFunc = (Func<TSource, TDestination>)_compiledConstructors.GetOrAdd(
                (sourceType, destType), _ => CompileConstructor<TSource, TDestination>(_configuration));
            var destination = constructFunc(source);

            if (_configuration.TryGetBeforeMap(sourceType, destType, out var before))
                ((Action<TSource, TDestination>)before!)(source, destination);

            if (!hasCustomCtor)
            {
                var assignAction = (Action<TSource, TDestination>)_compiledAssign.GetOrAdd(
                    (sourceType, destType), _ => CompileAssignment<TSource, TDestination>(_configuration, _serviceProvider, this));
                assignAction(source, destination);
            }

            if (_configuration.TryGetAfterMap(sourceType, destType, out var after))
                ((Action<TSource, TDestination>)after!)(source, destination);

            return destination;
        }

        /// <summary>
        /// Compiles a dispatcher for one <c>Include&lt;TDerivedSource,TDerivedDestination&gt;</c>
        /// registration: casts the boxed source to <paramref name="derivedSrc"/>,
        /// invokes this <see cref="Mapper"/> instance's own
        /// <see cref="Map{TSource,TDestination}(TSource)"/> for the derived pair
        /// (so the derived pair's own hooks/ConstructUsing/etc. still apply), and
        /// boxes the result back to <see cref="object"/>.
        /// </summary>
        /// <remarks>
        /// Uses reflection once, at first dispatch for this derived pair, to locate
        /// and close the generic <c>Map&lt;,&gt;(TSource)</c> overload — the same
        /// reflect-once-compile-once-cache-forever pattern as <see cref="CompileElementMapper"/>.
        /// <c>this</c> is captured as a compiled-in constant, which is safe because
        /// <see cref="Mapper"/> is designed to be a long-lived singleton.
        /// </remarks>
        private Func<object, object> CompilePolymorphicDispatch(Type derivedSrc, Type derivedDst)
        {
            var mapMethod = typeof(Mapper)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == nameof(Map) && m.IsGenericMethod && m.GetParameters().Length == 1)
                .MakeGenericMethod(derivedSrc, derivedDst);

            var sourceObjParam = Expression.Parameter(typeof(object), "source");
            var thisConst       = Expression.Constant(this);
            var castSource       = Expression.Convert(sourceObjParam, derivedSrc);
            var call             = Expression.Call(thisConst, mapMethod, castSource);
            var boxedResult      = Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object, object>>(boxedResult, sourceObjParam).Compile();
        }

        /// <summary>
        /// Returns the element/nested mapping delegate for a
        /// <paramref name="srcElem"/> → <paramref name="dstElem"/> pair, used by the
        /// collection and nested-member paths. When the pair has
        /// <c>Include&lt;&gt;</c> registrations the delegate dispatches on each
        /// element's runtime type (so a <c>Dog</c> in a <c>List&lt;Animal&gt;</c>, or a
        /// nested <c>Animal</c> member holding a <c>Dog</c>, maps to <c>DogDto</c>);
        /// otherwise it is the plain compiled base mapper with no per-element cost.
        /// </summary>
        private Delegate GetOrAddElementMapper(Type srcElem, Type dstElem)
            => _elementMappers.GetOrAdd((srcElem, dstElem), key =>
            {
                // The plain base delegate lives in _compiledMaps (shared with the
                // top-level base Map path); reuse or compile it there.
                var baseMapper = _compiledMaps.GetOrAdd(
                    key, k => CompileElementMapper(k.Item1, k.Item2, this, _configuration, _serviceProvider));

                if (_configuration.GetIncludedTypes(srcElem, dstElem).Length == 0)
                    return baseMapper;

                var make = typeof(Mapper)
                    .GetMethod(nameof(MakePolymorphicElementMapper), BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(srcElem, dstElem);
                return (Delegate)make.Invoke(this, [baseMapper])!;
            });

        /// <summary>
        /// Wraps <paramref name="baseMapper"/> in a runtime-type-dispatching delegate
        /// that reuses this instance's own <see cref="Map{TSource,TDestination}(TSource)"/>
        /// polymorphic machinery (<c>_polymorphicDispatch</c> +
        /// <see cref="CompilePolymorphicDispatch"/>) so a derived element maps through
        /// the derived pair's full pipeline (its own hooks / <c>ConstructUsing</c> /
        /// convention), exactly as a top-level <c>Map&lt;Base,BaseDto&gt;(derived)</c> call would.
        /// Genuinely-base elements fall straight through to <paramref name="baseMapper"/>
        /// after a single reference-equality type check.
        /// </summary>
        private Func<TSrcElem, TDstElem> MakePolymorphicElementMapper<TSrcElem, TDstElem>(
            Func<TSrcElem, TDstElem> baseMapper)
        {
            var srcElemType = typeof(TSrcElem);
            var included    = _configuration.GetIncludedTypes(srcElemType, typeof(TDstElem));

            return element =>
            {
                if (element is not null)
                {
                    var runtimeType = element.GetType();
                    if (runtimeType != srcElemType)
                    {
                        foreach (var (derivedSrc, derivedDst) in included)
                        {
                            if (!derivedSrc.IsInstanceOfType(element)) continue;

                            var dispatch = (Func<object, object>)_polymorphicDispatch.GetOrAdd(
                                (derivedSrc, derivedDst), _ => CompilePolymorphicDispatch(derivedSrc, derivedDst));

                            return (TDstElem)dispatch(element);
                        }
                    }
                }

                return baseMapper(element);
            };
        }

        // ── Collection merge compilation ─────────────────────────────────────────

        /// <summary>
        /// Clears <paramref name="destination"/> and repopulates it with mapped
        /// elements from <paramref name="source"/>. This is a wholesale
        /// replace-contents merge, not an identity-based add/update/remove diff.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="destination"/> reports <c>IsReadOnly</c>.
        /// Arrays implement <see cref="ICollection{T}"/> but throw
        /// <see cref="NotSupportedException"/> from <c>Clear</c>/<c>Add</c> — this
        /// check turns that into a clear, actionable message instead.
        /// </exception>
        private static void MergeCollection<TSrcElem, TDstElem>(
            IEnumerable<TSrcElem> source, ICollection<TDstElem> destination, Func<TSrcElem, TDstElem> elementMapper)
        {
            if (destination.IsReadOnly)
                throw new InvalidOperationException(
                    $"Map(source, destination) requires a mutable ICollection<{typeof(TDstElem).Name}> " +
                    $"destination — '{destination.GetType().Name}' reports IsReadOnly = true (arrays and " +
                    $"read-only sequences cannot be cleared and repopulated).");

            destination.Clear();
            foreach (var element in source)
                destination.Add(elementMapper(element));
        }

        /// <summary>
        /// Diffs <paramref name="source"/> against <paramref name="destination"/> by
        /// identity instead of clearing and rebuilding wholesale: matched elements
        /// (per <paramref name="matches"/>) are updated in place via
        /// <paramref name="assignInto"/>, destination elements with no matching
        /// source element are removed, and source elements with no matching
        /// destination element are mapped fresh via <paramref name="createNew"/> and added.
        /// </summary>
        /// <remarks>
        /// Single-pass linear scan (each source element is matched against
        /// not-yet-consumed destination elements) — the same complexity class as
        /// AutoMapper's own default comparer-based collection merge.
        /// </remarks>
        private static void MergeCollectionByIdentity<TSrcElem, TDstElem>(
            IEnumerable<TSrcElem>            source,
            ICollection<TDstElem>            destination,
            Func<TSrcElem, TDstElem, bool>   matches,
            Action<TSrcElem, TDstElem>       assignInto,
            Func<TSrcElem, TDstElem>         createNew)
        {
            if (destination.IsReadOnly)
                throw new InvalidOperationException(
                    $"Map(source, destination) requires a mutable ICollection<{typeof(TDstElem).Name}> " +
                    $"destination — '{destination.GetType().Name}' reports IsReadOnly = true (arrays and " +
                    $"read-only sequences cannot be diffed and updated in place).");

            var destList     = destination.ToList();
            var consumedDest = new bool[destList.Count];
            var toAdd        = new List<TDstElem>();

            foreach (var srcElement in source)
            {
                var matchedIndex = -1;
                for (var i = 0; i < destList.Count; i++)
                {
                    if (consumedDest[i]) continue;
                    if (!matches(srcElement, destList[i])) continue;
                    matchedIndex = i;
                    break;
                }

                if (matchedIndex >= 0)
                {
                    assignInto(srcElement, destList[matchedIndex]);
                    consumedDest[matchedIndex] = true;
                }
                else
                {
                    toAdd.Add(createNew(srcElement));
                }
            }

            // Remove destination elements that no source element matched.
            for (var i = 0; i < destList.Count; i++)
                if (!consumedDest[i])
                    destination.Remove(destList[i]);

            foreach (var newElement in toAdd)
                destination.Add(newElement);
        }

        /// <summary>
        /// Compiles an <c>Action&lt;TSource,TDestination&gt;</c> that merges a
        /// source sequence into an existing destination collection — via
        /// <see cref="MergeCollectionByIdentity{TSrcElem,TDstElem}"/> when an
        /// <see cref="IMappingExpression{TSource,TDestination}.EqualityComparison"/>
        /// is registered for the element pair, otherwise via the wholesale
        /// clear-and-rebuild <see cref="MergeCollection{TSrcElem,TDstElem}"/>. Backs
        /// the collection branch of <see cref="Map{TSource,TDestination}(TSource,TDestination)"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <typeparamref name="TDestination"/> does not implement
        /// <c>ICollection&lt;TDstElem&gt;</c> (e.g. an array or a read-only
        /// sequence) and therefore cannot be cleared/diffed and repopulated.
        /// </exception>
        private static Action<TSource, TDestination> CompileCollectionMerge<TSource, TDestination>(
            Type   srcElemType,
            Type   dstElemType,
            Mapper owner)
        {
            var configuration   = owner._configuration;
            var serviceProvider = owner._serviceProvider;
            var destType        = typeof(TDestination);
            var iCollectionDst  = typeof(ICollection<>).MakeGenericType(dstElemType);

            if (!iCollectionDst.IsAssignableFrom(destType))
                throw new InvalidOperationException(
                    $"Map(source, destination) requires the destination collection type '{destType.Name}' " +
                    $"to implement ICollection<{dstElemType.Name}> (e.g. List<{dstElemType.Name}>) so it can " +
                    $"be cleared and repopulated. Arrays and read-only sequences are not supported.");

            var sourceType     = typeof(TSource);
            var iEnumerableSrc = typeof(IEnumerable<>).MakeGenericType(srcElemType);

            var sourceParam = Expression.Parameter(sourceType, "source");
            var destParam   = Expression.Parameter(destType, "destination");

            Expression sourceEnum = sourceType == iEnumerableSrc
                ? (Expression)sourceParam
                : Expression.Convert(sourceParam, iEnumerableSrc);

            Expression destColl = destType == iCollectionDst
                ? (Expression)destParam
                : Expression.Convert(destParam, iCollectionDst);

            Expression call;

            if (configuration.TryGetEqualityComparer(srcElemType, dstElemType, out var comparer))
            {
                var comparerFuncType = typeof(Func<,,>).MakeGenericType(srcElemType, dstElemType, typeof(bool));
                var assignFuncType   = typeof(Action<,>).MakeGenericType(srcElemType, dstElemType);
                var createFuncType   = typeof(Func<,>).MakeGenericType(srcElemType, dstElemType);

                // Newly-added source elements map through the polymorphism-aware
                // element mapper, so an added derived element (e.g. a Dog appearing in
                // the source) lands as its derived DTO.
                var elementMapper = owner.GetOrAddElementMapper(srcElemType, dstElemType);

                // Reflectively compile the element pair's assign-into-existing delegate
                // — the same CompileAssignment already backing Map(source,destination)
                // for object pairs — so updated elements go through the full Phase
                // 0-3 convention pipeline (ForMember overrides, Condition, etc.), not
                // just a fresh construction. (Matched-in-place updates keep the element
                // pair's static types — an existing destination element's runtime type
                // cannot change — so this stays the base assign, not a polymorphic one.)
                var compileAssignMethod = typeof(Mapper)
                    .GetMethod(nameof(CompileAssignment), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(srcElemType, dstElemType);
                var assignInto = (Delegate)compileAssignMethod.Invoke(null, [configuration, serviceProvider, owner])!;

                var mergeMethod = typeof(Mapper)
                    .GetMethod(nameof(MergeCollectionByIdentity), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(srcElemType, dstElemType);

                call = Expression.Call(
                    mergeMethod,
                    sourceEnum,
                    destColl,
                    Expression.Constant(comparer, comparerFuncType),
                    Expression.Constant(assignInto, assignFuncType),
                    Expression.Constant(elementMapper, createFuncType));
            }
            else
            {
                var funcType = typeof(Func<,>).MakeGenericType(srcElemType, dstElemType);
                var elementMapper = owner.GetOrAddElementMapper(srcElemType, dstElemType);

                var mergeMethod = typeof(Mapper)
                    .GetMethod(nameof(MergeCollection), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(srcElemType, dstElemType);

                call = Expression.Call(
                    mergeMethod, sourceEnum, destColl, Expression.Constant(elementMapper, funcType));
            }

            return Expression.Lambda<Action<TSource, TDestination>>(call, sourceParam, destParam).Compile();
        }

        // ── Expression Tree compilation engine ──────────────────────────────────

        /// <summary>
        /// Entry point: creates a fresh DFS path tracker and delegates to
        /// <see cref="BuildBindings"/> to compile the full mapping lambda.
        /// </summary>
        private static Func<TSource, TDestination> CompileMapping<TSource, TDestination>(
            MapperConfiguration configuration, IServiceProvider? serviceProvider, Mapper? owner)
        {
            var sourceType  = typeof(TSource);
            var destType    = typeof(TDestination);
            var sourceParam = Expression.Parameter(sourceType, "source");

            // visitedPath tracks how many times each (SourceType, DestType) pair is
            // currently on the DFS stack. A pair reaching its configured MaxDepth
            // signals a back-edge (cycle, or a depth limit reached by design).
            var visitedPath = new Dictionary<(Type, Type), int>();

            Expression body;
            if (configuration.TryGetCustomConstructor(sourceType, destType, out var customCtor))
            {
                // ConstructUsing is authoritative: the lambda supplies the entire
                // construction expression (optionally with its own object
                // initializer), so convention-based binding is skipped entirely.
                body = InlineLambdaBody(customCtor!, sourceParam);
            }
            else
            {
                var bindings = BuildBindings(sourceType, destType, sourceParam, visitedPath, configuration, serviceProvider, owner);
                body = Expression.MemberInit(Expression.New(destType), bindings);
            }

            var lambda = Expression.Lambda<Func<TSource, TDestination>>(body, sourceParam);
            return lambda.Compile();
        }

        /// <summary>
        /// Compiles a bare "construct a blank destination" delegate: either the
        /// registered <c>ConstructUsing</c> expression, or <c>new TDestination()</c>
        /// ignoring the source. Used only by the hook-aware dispatch path, where
        /// construction and property assignment happen as separate steps so that
        /// <c>BeforeMap</c> can run on an already-constructed-but-unpopulated instance.
        /// </summary>
        private static Func<TSource, TDestination> CompileConstructor<TSource, TDestination>(
            MapperConfiguration configuration)
        {
            var sourceType  = typeof(TSource);
            var destType    = typeof(TDestination);
            var sourceParam = Expression.Parameter(sourceType, "source");

            Expression body = configuration.TryGetCustomConstructor(sourceType, destType, out var customCtor)
                ? InlineLambdaBody(customCtor!, sourceParam)
                : Expression.New(destType);

            return Expression.Lambda<Func<TSource, TDestination>>(body, sourceParam).Compile();
        }

        /// <summary>
        /// Compiles an <c>Action&lt;TSource,TDestination&gt;</c> that assigns
        /// convention-matched (and <c>ForMember</c>-overridden) properties directly
        /// onto an existing destination instance, instead of building a fresh
        /// <c>MemberInit</c>. Backs both <see cref="Map{TSource,TDestination}(TSource,TDestination)"/>
        /// and the hook-aware dispatch path.
        /// </summary>
        private static Action<TSource, TDestination> CompileAssignment<TSource, TDestination>(
            MapperConfiguration configuration, IServiceProvider? serviceProvider, Mapper? owner)
        {
            var sourceType  = typeof(TSource);
            var destType    = typeof(TDestination);
            var sourceParam = Expression.Parameter(sourceType, "source");
            var destParam   = Expression.Parameter(destType, "destination");
            var visitedPath = new Dictionary<(Type, Type), int>();

            var statements = BuildAssignments(sourceType, destType, sourceParam, destParam, visitedPath, configuration, serviceProvider, owner);
            Expression body = statements.Count > 0 ? Expression.Block(statements) : Expression.Empty();

            return Expression.Lambda<Action<TSource, TDestination>>(body, sourceParam, destParam).Compile();
        }

        /// <summary>
        /// One resolved destination-member binding: the property, its final
        /// (already type-adapted) value expression, and an optional per-member
        /// <c>Condition</c> predicate. Shared by both <see cref="BuildBindings"/>
        /// (which wraps it as a <see cref="MemberBinding"/> for <c>MemberInit</c>)
        /// and <see cref="BuildAssignments"/> (which wraps it as an <c>Assign</c>
        /// statement) so the four matching phases are implemented exactly once.
        /// </summary>
        private readonly record struct MemberBindingPlan(PropertyInfo DestProp, Expression Value, LambdaExpression? Condition);

        /// <summary>
        /// DFS-aware core that resolves destination-member bindings for a
        /// <paramref name="sourceType"/> → <paramref name="destType"/> pair.
        /// </summary>
        /// <remarks>
        /// DFS contract:
        /// <list type="bullet">
        ///   <item>
        ///     <b>Tree-edge</b>: pair below its configured MaxDepth →
        ///     depth incremented at entry, decremented in the <c>finally</c> block (backtracking).
        ///   </item>
        ///   <item>
        ///     <b>Back-edge</b>: pair already at MaxDepth → cycle (or depth limit)
        ///     detected; returns an empty list so the caller can emit <c>null</c> or skip the binding.
        ///   </item>
        /// </list>
        /// </remarks>
        private static List<MemberBindingPlan> BuildBindingPlans(
            Type                          sourceType,
            Type                          destType,
            Expression                    sourceExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           configuration,
            IServiceProvider?             serviceProvider,
            Mapper?                       owner)
        {
            var pair     = (sourceType, destType);
            var maxDepth = configuration.GetMaxDepth(sourceType, destType);

            // ── Back-edge guard ───────────────────────────────────────────────────
            // If this pair is already at its configured depth we have a cycle
            // (or intentionally reached the configured recursion limit).
            visitedPath.TryGetValue(pair, out var currentDepth);
            if (currentDepth >= maxDepth)
                return [];

            visitedPath[pair] = currentDepth + 1;

            try
            {
                var sourceIndex    = BuildSourceIndex(sourceType);
                var sourceTrie     = GetOrBuildTrie(sourceType, configuration.GetFlattenDepth(sourceType, destType));
                var plans          = new List<MemberBindingPlan>();
                var boundDestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Exclude indexed properties (e.g. List<T>'s Item indexer): they
                // require arguments and cannot be used with Expression.Property().
                var destProps      = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(p => p.GetIndexParameters().Length == 0)
                                            .ToArray();

                // ── Phase 0: ForMember overrides (Ignore / MapFrom / Condition / NullSubstitute) ──
                foreach (var destProp in destProps)
                {
                    if (!destProp.CanWrite) continue;
                    if (!configuration.TryGetMemberOverride(
                            sourceType, destType, destProp.Name, out var memberOverride)) continue;

                    // Mark as handled regardless of Ignore/MapFrom so convention
                    // phases never touch this property.
                    boundDestNames.Add(destProp.Name);

                    if (memberOverride!.IsIgnored) continue;

                    Expression? valueExpr = null;
                    Type?       valueType = null;

                    if (memberOverride.MapFromExpression is not null)
                    {
                        valueExpr = InlineLambdaBody(memberOverride.MapFromExpression, sourceExpr);
                        valueType = valueExpr.Type;
                    }
                    else if (memberOverride.ResolverType is not null)
                    {
                        // DI-resolved value resolver: resolve a fresh instance from the
                        // Mapper's IServiceProvider on every map call (correct for
                        // scoped/transient dependencies), unlike the caller-constructed
                        // MapFrom(IValueResolver<,>) overload, which captures one instance.
                        if (serviceProvider is null)
                            throw new InvalidOperationException(
                                $"'{sourceType.Name} -> {destType.Name}.{destProp.Name}' uses " +
                                $"MapFrom<{memberOverride.ResolverType.Name}>() (a DI-resolved value resolver), " +
                                $"but this Mapper was constructed without an IServiceProvider. " +
                                $"Use new Mapper(configuration, serviceProvider) instead.");

                        var getRequiredService = typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions)
                            .GetMethod(
                                nameof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService),
                                [typeof(IServiceProvider), typeof(Type)])!;

                        // GetRequiredService(IServiceProvider, Type) is an extension method —
                        // a static method under the hood — so both arguments are passed
                        // positionally via the static Expression.Call overload, not as an
                        // "instance" receiver.
                        var getServiceCall = Expression.Call(
                            getRequiredService,
                            Expression.Constant(serviceProvider, typeof(IServiceProvider)),
                            Expression.Constant(memberOverride.ResolverType, typeof(Type)));

                        // Cast to the closed IValueResolver<TSource,TMember> interface (not the
                        // concrete resolver type) — matches exactly how the caller-constructed
                        // MapFrom(IValueResolver<,>) overload already resolves its Resolve call.
                        var resolverInterfaceType = typeof(IValueResolver<,>).MakeGenericType(sourceType, memberOverride.ResolverMemberType!);
                        var castResolver  = Expression.Convert(getServiceCall, resolverInterfaceType);
                        var resolveMethod = resolverInterfaceType.GetMethod(nameof(IValueResolver<object, object>.Resolve))!;

                        valueExpr = Expression.Call(castResolver, resolveMethod, sourceExpr);
                        valueType = memberOverride.ResolverMemberType;
                    }
                    else if ((memberOverride.HasNullSubstitute || memberOverride.ConditionExpression is not null)
                             && sourceIndex.TryGetValue(destProp.Name.ToUpperInvariant(), out var namedSourceProp))
                    {
                        // Condition/NullSubstitute without an explicit MapFrom fall
                        // back to the convention-matched source property of the same name.
                        valueExpr = Expression.Property(sourceExpr, namedSourceProp);
                        valueType = namedSourceProp.PropertyType;
                    }

                    if (valueExpr is null) continue;

                    if (memberOverride.HasNullSubstitute && CanBeNull(valueType!))
                    {
                        var substituteConst = Expression.Constant(
                            memberOverride.NullSubstituteValue, memberOverride.NullSubstituteType!);

                        if (!TryAdaptExpression(
                                substituteConst, memberOverride.NullSubstituteType!, valueType!, out var adaptedSubstitute))
                            adaptedSubstitute = substituteConst;

                        valueExpr = Expression.Condition(
                            Expression.Equal(valueExpr, Expression.Constant(null, valueType!)),
                            adaptedSubstitute!,
                            valueExpr);
                    }

                    if (!TryAdaptExpression(valueExpr, valueType!, destProp.PropertyType, out var adapted)) continue;

                    plans.Add(new MemberBindingPlan(destProp, adapted!, memberOverride.ConditionExpression));
                }

                // ── Phase 1: Direct, case-insensitive name match ──────────────────
                foreach (var destProp in destProps)
                {
                    if (!destProp.CanWrite) continue;
                    if (boundDestNames.Contains(destProp.Name)) continue;   // Phase 0 already handled
                    if (!sourceIndex.TryGetValue(destProp.Name.ToUpperInvariant(), out var sourceProp)) continue;

                    // Compiled to a direct IL call/ldfld — never GetValue().
                    Expression sourceAccess = Expression.Property(sourceExpr, sourceProp);

                    // Fast path: scalar, enum, numeric, nullable, or directly assignable.
                    if (TryAdaptExpression(sourceAccess, sourceProp.PropertyType, destProp.PropertyType, out var adapted))
                    {
                        plans.Add(new MemberBindingPlan(destProp, adapted!, null));
                        boundDestNames.Add(destProp.Name);
                        continue;
                    }

                    // Slow path: matching names but incompatible complex types.
                    // Recurse into the sub-graph with full cycle detection.
                    var nestedExpr = TryBuildNestedObjectExpression(
                        sourceProp.PropertyType, destProp.PropertyType, sourceAccess, visitedPath, configuration, serviceProvider, owner);
                    if (nestedExpr is null) continue;

                    plans.Add(new MemberBindingPlan(destProp, nestedExpr, null));
                    boundDestNames.Add(destProp.Name);
                }

                // ── Phase 2: Flattening (e.g. source.Customer.Name → dest.CustomerName) ─
                foreach (var destProp in destProps)
                {
                    if (!destProp.CanWrite || boundDestNames.Contains(destProp.Name)) continue;

                    if (!TryBuildFlattenedSourcePath(sourceExpr, sourceTrie, destProp.Name,
                            out var flatExpr, out var flatType))
                        continue;

                    if (!TryAdaptExpression(flatExpr!, flatType!, destProp.PropertyType, out var adapted))
                        continue;

                    plans.Add(new MemberBindingPlan(destProp, adapted!, null));
                    boundDestNames.Add(destProp.Name);
                }

                // ── Phase 3: Unflattening (e.g. source.CustomerName → dest.Customer.Name) ─
                foreach (var destProp in destProps)
                {
                    if (!destProp.CanWrite || boundDestNames.Contains(destProp.Name)) continue;

                    var subType = destProp.PropertyType;
                    if (subType.IsValueType || subType == typeof(string)) continue;
                    if (subType.GetConstructor(Type.EmptyTypes) is null) continue;

                    var subBindings = BuildUnflattenBindings(sourceExpr, sourceIndex, destProp.Name, subType);
                    if (subBindings.Count == 0) continue;

                    var subInit = Expression.MemberInit(Expression.New(subType), subBindings);
                    plans.Add(new MemberBindingPlan(destProp, subInit, null));
                    boundDestNames.Add(destProp.Name);
                }

                return plans;
            }
            finally
            {
                // ── Backtrack: decrement so sibling/ancestor branches see the right depth ─
                var remaining = visitedPath[pair] - 1;
                if (remaining <= 0) visitedPath.Remove(pair);
                else visitedPath[pair] = remaining;
            }
        }

        /// <summary>
        /// Resolves <see cref="BuildBindingPlans"/> into <see cref="MemberBinding"/>
        /// nodes for a <c>MemberInit</c> (fresh-instance) compilation. A per-member
        /// <c>Condition</c> is rendered as a ternary defaulting to
        /// <see langword="default"/> — there is nothing else to leave a freshly
        /// constructed member as.
        /// </summary>
        private static List<MemberBinding> BuildBindings(
            Type                          sourceType,
            Type                          destType,
            Expression                    sourceExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           configuration,
            IServiceProvider?             serviceProvider,
            Mapper?                       owner)
        {
            var plans    = BuildBindingPlans(sourceType, destType, sourceExpr, visitedPath, configuration, serviceProvider, owner);
            var bindings = new List<MemberBinding>(plans.Count);

            foreach (var plan in plans)
            {
                var value = plan.Condition is null
                    ? plan.Value
                    : Expression.Condition(
                          InlineLambdaBody(plan.Condition, sourceExpr),
                          plan.Value,
                          Expression.Default(plan.DestProp.PropertyType));

                bindings.Add(Expression.Bind(plan.DestProp, value));
            }

            return bindings;
        }

        /// <summary>
        /// Resolves <see cref="BuildBindingPlans"/> into <c>Assign</c> statements
        /// against an existing <paramref name="destExpr"/> instance. A per-member
        /// <c>Condition</c> wraps the entire assignment in an <c>IfThen</c> — when
        /// the predicate is false the statement is skipped altogether, leaving the
        /// existing destination value untouched (unlike the fresh-instance
        /// <see cref="BuildBindings"/> path, there is something meaningful to
        /// preserve here).
        /// </summary>
        private static List<Expression> BuildAssignments(
            Type                          sourceType,
            Type                          destType,
            Expression                    sourceExpr,
            Expression                    destExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           configuration,
            IServiceProvider?             serviceProvider,
            Mapper?                       owner)
        {
            var plans      = BuildBindingPlans(sourceType, destType, sourceExpr, visitedPath, configuration, serviceProvider, owner);
            var statements = new List<Expression>(plans.Count);

            foreach (var plan in plans)
            {
                var assign = Expression.Assign(Expression.Property(destExpr, plan.DestProp), plan.Value);

                statements.Add(plan.Condition is null
                    ? assign
                    : Expression.IfThen(InlineLambdaBody(plan.Condition, sourceExpr), assign));
            }

            return statements;
        }

        /// <summary>
        /// Attempts to build a null-guarded <see cref="Expression.MemberInit"/> for a
        /// nested complex-type property pair, respecting the current DFS path.
        /// </summary>
        /// <returns>
        /// <list type="bullet">
        ///   <item>
        ///     <see cref="Expression.Constant"/>(<see langword="null"/>, <paramref name="dstType"/>)
        ///     when a back-edge (cycle, or configured depth limit) is detected — terminates the branch safely.
        ///   </item>
        ///   <item>
        ///     A <see cref="BlockExpression"/> containing a temp-variable null-guard
        ///     around a <see cref="NewExpression"/>/<c>MemberInit</c> (or, when a
        ///     <c>ConstructUsing</c> expression is registered for this pair, the
        ///     inlined constructor expression) when mapping succeeds.
        ///   </item>
        ///   <item>
        ///     <see langword="null"/> when the types are not suitable for deep mapping
        ///     or no matching properties were found (binding is silently skipped).
        ///   </item>
        /// </list>
        /// </returns>
        private static Expression? TryBuildNestedObjectExpression(
            Type                          srcType,
            Type                          dstType,
            Expression                    srcExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           configuration,
            IServiceProvider?             serviceProvider,
            Mapper?                       owner)
        {
            if (srcType.IsValueType || srcType == typeof(string)) return null;
            if (dstType.IsValueType || dstType == typeof(string)) return null;

            // ── Polymorphic nested member (Include<> registered on this pair) ─────
            // Route through the owner's runtime-dispatching element mapper so a nested
            // member whose runtime value is a derived type (e.g. an Animal property
            // holding a Dog) maps to the derived DTO. Emitted as a null-guarded call
            // to that delegate. Strictly gated on there being Include<> registrations,
            // so ordinary nested members keep the inlined MemberInit below (with its
            // shared DFS cycle/MaxDepth tracking) untouched.
            if (owner is not null && configuration.GetIncludedTypes(srcType, dstType).Length > 0)
            {
                var polyMapper = owner.GetOrAddElementMapper(srcType, dstType);
                var polyFunc   = typeof(Func<,>).MakeGenericType(srcType, dstType);

                var polyTmp    = Expression.Variable(srcType, "_poly" + srcType.Name);
                var polyAssign = Expression.Assign(polyTmp, srcExpr);
                var polyCall   = Expression.Invoke(Expression.Constant(polyMapper, polyFunc), polyTmp);
                var polyCond   = Expression.Condition(
                    Expression.Equal(polyTmp, Expression.Constant(null, srcType)),
                    Expression.Constant(null, dstType),
                    polyCall);

                return Expression.Block(dstType, [polyTmp], polyAssign, polyCond);
            }

            var hasCustomCtor = configuration.TryGetCustomConstructor(srcType, dstType, out var customCtor);
            if (!hasCustomCtor && dstType.GetConstructor(Type.EmptyTypes) is null) return null;

            // ── Back-edge: cycle (or configured depth limit) reached ──────────────
            // Only applies to convention-based recursion: a ConstructUsing lambda
            // is a fixed, non-recursive expression and cannot itself infinite-loop.
            if (!hasCustomCtor)
            {
                var maxDepth = configuration.GetMaxDepth(srcType, dstType);
                visitedPath.TryGetValue((srcType, dstType), out var currentDepth);
                if (currentDepth >= maxDepth)
                    return Expression.Constant(null, dstType);
            }

            // Use a temp variable so the source getter is invoked exactly once even
            // though its value is needed in both the null-check and the init branches.
            var tempVar    = Expression.Variable(srcType, "_" + srcType.Name.ToLowerInvariant());
            var assignTemp = Expression.Assign(tempVar, srcExpr);

            Expression constructedExpr;
            if (hasCustomCtor)
            {
                constructedExpr = InlineLambdaBody(customCtor!, tempVar);
            }
            else
            {
                // Recurse — BuildBindingPlans will track (srcType, dstType) depth and
                // backtrack when it returns.
                var nestedBindings = BuildBindings(srcType, dstType, tempVar, visitedPath, configuration, serviceProvider, owner);

                // No properties mapped (either nothing matched or depth-limited emptied
                // the list). Skip the binding entirely rather than emitting a hollow `new DstType {}`.
                if (nestedBindings.Count == 0) return null;

                constructedExpr = Expression.MemberInit(Expression.New(dstType), nestedBindings);
            }

            // { var _tmp = srcExpr; _tmp == null ? (DstType)null : <constructed> }
            var nullCheck   = Expression.Equal(tempVar, Expression.Constant(null, srcType));
            var conditional = Expression.Condition(
                nullCheck,
                Expression.Constant(null, dstType),
                constructedExpr);

            return Expression.Block(dstType, [tempVar], assignTemp, conditional);
        }

        // ── Collection mapping compilation ───────────────────────────────────────

        /// <summary>
        /// Compiles a mapping delegate for a collection-to-collection conversion.
        /// The emitted IL is equivalent to:
        /// <code>source.Select(elementMapper).ToList()</code>
        /// or <c>.ToArray()</c> when <typeparamref name="TDestination"/> is an array,
        /// or a plain cast to <c>IEnumerable&lt;TDestElement&gt;</c> otherwise.
        /// </summary>
        /// <remarks>
        /// The element-level delegate comes from <see cref="GetOrAddElementMapper"/>,
        /// so it dispatches on each element's runtime type when the element pair has
        /// <c>Include&lt;&gt;</c> registrations (a <c>Dog</c> in a <c>List&lt;Animal&gt;</c>
        /// maps to <c>DogDto</c>) and is the plain compiled base mapper otherwise. It is
        /// cached, so the element mapper is never recompiled across repeated calls.
        /// </remarks>
        private static Func<TSource, TDestination> CompileCollectionMapping<TSource, TDestination>(
            Type   srcElemType,
            Type   dstElemType,
            Mapper owner)
        {
            // Retrieve or compile the (possibly polymorphism-aware) Func<TSrcElem, TDstElem>.
            var elementMapper = owner.GetOrAddElementMapper(srcElemType, dstElemType);

            var sourceType      = typeof(TSource);
            var destType        = typeof(TDestination);
            var iEnumerableSrc  = typeof(IEnumerable<>).MakeGenericType(srcElemType);
            var funcType        = typeof(Func<,>).MakeGenericType(srcElemType, dstElemType);
            var sourceParam     = Expression.Parameter(sourceType, "source");

            // Cast the source to IEnumerable<TSrcElem> so Enumerable.Select can bind.
            Expression sourceEnum = sourceType == iEnumerableSrc
                ? (Expression)sourceParam
                : Expression.Convert(sourceParam, iEnumerableSrc);

            // Locate Enumerable.Select<TSrc,TDst>(IEnumerable<TSrc>, Func<TSrc,TDst>).
            var selectMethod = typeof(Enumerable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Enumerable.Select)
                         && m.GetParameters() is { Length: 2 } ps
                         && ps[1].ParameterType.IsGenericType
                         && ps[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>))
                .MakeGenericMethod(srcElemType, dstElemType);

            // source.Select(elementMapper)
            var selectCall = Expression.Call(
                selectMethod,
                sourceEnum,
                Expression.Constant(elementMapper, funcType));

            // Materialise to the correct destination collection type.
            Expression body;
            if (destType.IsArray)
            {
                // .ToArray<TDstElem>()
                var toArray = typeof(Enumerable)
                    .GetMethod(nameof(Enumerable.ToArray))!
                    .MakeGenericMethod(dstElemType);
                body = Expression.Call(toArray, selectCall);
            }
            else
            {
                // .ToList<TDstElem>() — covers List<T>, IList<T>, ICollection<T>, IEnumerable<T>
                var toList = typeof(Enumerable)
                    .GetMethod(nameof(Enumerable.ToList))!
                    .MakeGenericMethod(dstElemType);
                body = Expression.Call(toList, selectCall);
            }

            // Widen to TDestination (e.g. List<TDst> → IEnumerable<TDst>).
            if (body.Type != destType)
                body = Expression.Convert(body, destType);

            return Expression.Lambda<Func<TSource, TDestination>>(body, sourceParam).Compile();
        }

        /// <summary>
        /// Uses reflection to invoke <see cref="CompileMapping{TSource,TDestination}"/>
        /// with runtime-only type arguments and returns the resulting delegate.
        /// Called at most once per element-type pair thanks to the
        /// <c>_compiledMaps</c> cache.
        /// </summary>
        private static Delegate CompileElementMapper(
            Type                srcElemType,
            Type                dstElemType,
            Mapper?             owner,
            MapperConfiguration configuration,
            IServiceProvider?   serviceProvider)
        {
            var method = typeof(Mapper)
                .GetMethod(nameof(CompileMapping),
                           BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(srcElemType, dstElemType);

            return (Delegate)method.Invoke(null, [configuration, serviceProvider, owner])!;
        }

        /// <summary>
        /// Returns <see langword="true"/> and sets <paramref name="elementType"/>
        /// when <paramref name="type"/> is a sequence type:
        /// <list type="bullet">
        ///   <item><c>T[]</c> — element type is <c>T</c></item>
        ///   <item><c>IEnumerable&lt;T&gt;</c> (or any closed-generic variant) —
        ///   element type is the single generic argument</item>
        ///   <item>Any type that <em>implements</em> <c>IEnumerable&lt;T&gt;</c> —
        ///   e.g. <c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c></item>
        /// </list>
        /// <see cref="string"/> is explicitly excluded even though it implements
        /// <c>IEnumerable&lt;char&gt;</c>.
        /// </summary>
        private static bool TryGetCollectionElementType(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? elementType)
        {
            if (type == typeof(string))
            {
                elementType = null;
                return false;
            }

            // T[]
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return elementType is not null;
            }

            // IEnumerable<T> itself
            if (type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }

            // Any type that implements IEnumerable<T> (e.g. List<T>, Collection<T>)
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType &&
                    iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    elementType = iface.GetGenericArguments()[0];
                    return true;
                }
            }

            elementType = null;
            return false;
        }

        // ── Trie construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns a cached <see cref="PropertyTrieNode"/> root for
        /// <paramref name="sourceType"/> at the given flatten/unflatten lookahead
        /// <paramref name="depth"/>, building it on first access. Different depths
        /// for the same source type (via <c>FlattenDepth</c>) are cached separately.
        /// </summary>
        private static PropertyTrieNode GetOrBuildTrie(Type sourceType, int depth)
            => _trieCache.GetOrAdd((sourceType, depth), static key =>
            {
                var root = new PropertyTrieNode();
                // Seed the visited-on-path set with the root type to prevent
                // direct self-referential cycles from the very first level.
                PopulateTrie(root, key.Item1, depthRemaining: key.Item2, visitedOnPath: [key.Item1]);
                return root;
            });

        /// <summary>
        /// Recursively walks <paramref name="type"/>'s readable public properties and
        /// adds a child <see cref="PropertyTrieNode"/> for each one, then descends
        /// into complex sub-types.
        /// </summary>
        /// <param name="node">The trie node whose children are being populated.</param>
        /// <param name="type">The CLR type whose properties are walked at this level.</param>
        /// <param name="depthRemaining">Remaining recursion budget for this branch.</param>
        /// <param name="visitedOnPath">
        /// Types already on the current ancestor path.  Prevents cyclic graphs from
        /// causing infinite recursion while still allowing the same type to appear in
        /// sibling branches (because we remove the type when backtracking).
        /// </param>
        private static void PopulateTrie(
            PropertyTrieNode node,
            Type             type,
            int              depthRemaining,
            HashSet<Type>    visitedOnPath)
        {
            if (depthRemaining == 0) return;

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;

                var child = new PropertyTrieNode { Property = prop };
                node.Children[prop.Name] = child;

                var propType = prop.PropertyType;

                // Only recurse into non-primitive reference types that have not yet
                // been visited on this ancestor path (cycle guard).
                if (!propType.IsValueType
                    && propType != typeof(string)
                    && !propType.IsArray
                    && !propType.IsGenericType
                    && visitedOnPath.Add(propType))
                {
                    PopulateTrie(child, propType, depthRemaining - 1, visitedOnPath);
                    visitedOnPath.Remove(propType); // backtrack: allow type in sibling branches
                }
            }
        }

        // ── Flattening ───────────────────────────────────────────────────────────

        /// <summary>
        /// Entry point for Phase 2. Resolves a flat destination property name
        /// (e.g. <c>"CustomerName"</c>) to a navigated expression path on the source
        /// object graph (e.g. <c>source.Customer.Name</c>) by walking the pre-built
        /// <see cref="PropertyTrieNode"/> trie.
        /// </summary>
        private static bool TryBuildFlattenedSourcePath(
            Expression       sourceExpr,
            PropertyTrieNode trieRoot,
            string           destName,
            out Expression?  result,
            out Type?        resultType)
            => TryTraverseTrie(sourceExpr, trieRoot, destName.AsSpan(),
                               isTopLevel: true, out result, out resultType);

        /// <summary>
        /// Recursively traverses <paramref name="currentNode"/>'s children against
        /// <paramref name="remaining"/> using <see cref="ReadOnlySpan{T}"/> slicing.
        /// </summary>
        /// <remarks>
        /// Complexity: O(L) where L = <c>destName.Length</c>.
        /// At each node we consume exactly as many characters as the matching child
        /// key is long, so the total characters processed across all recursive calls
        /// equals L.  All comparisons use <see cref="MemoryExtensions.Equals"/> on
        /// span slices — no <c>string.Split</c>, no <c>StartsWith</c>, no heap
        /// substring is ever created during traversal.
        /// </remarks>
        private static bool TryTraverseTrie(
            Expression         currentExpr,
            PropertyTrieNode   currentNode,
            ReadOnlySpan<char> remaining,
            bool               isTopLevel,
            out Expression?    result,
            out Type?          resultType)
        {
            foreach (var (key, childNode) in currentNode.Children)
            {
                // Fast-reject: key cannot match if it is longer than what remains.
                if (remaining.Length < key.Length) continue;

                // Zero-allocation span comparison — no heap string is created here.
                if (!remaining[..key.Length].Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var prop          = childNode.Property!;
                var propType      = prop.PropertyType;
                var nextRemaining = remaining[key.Length..];

                // Phase 1 owns exact single-segment matches; skip them at the top level.
                if (isTopLevel && nextRemaining.IsEmpty) continue;

                if (nextRemaining.IsEmpty)
                {
                    // ── Leaf: the entire destination name has been consumed ────────
                    result     = Expression.Property(currentExpr, prop);
                    resultType = propType;
                    return true;
                }

                // ── Non-leaf: cross a segment boundary into the sub-object ─────────
                // A temp variable ensures the intermediate getter is called exactly
                // once in the emitted IL, even though it appears in both the
                // null-check condition and the value branch.
                var tempVar    = Expression.Variable(propType, "_" + key.ToLowerInvariant());
                var assignTemp = Expression.Assign(tempVar, Expression.Property(currentExpr, prop));

                if (!TryTraverseTrie(tempVar, childNode, nextRemaining,
                                     isTopLevel: false, out var inner, out var innerType))
                    continue;

                Expression blockBody;
                if (!propType.IsValueType)
                {
                    // { var tmp = currentExpr.Prop; tmp == null ? default : inner(tmp) }
                    var nullCheck = Expression.Equal(tempVar, Expression.Constant(null, propType));
                    blockBody     = Expression.Condition(nullCheck, Expression.Default(innerType!), inner!);
                }
                else
                {
                    blockBody = inner!;
                }

                result     = Expression.Block(innerType!, [tempVar], assignTemp, blockBody);
                resultType = innerType;
                return true;
            }

            result     = null;
            resultType = null;
            return false;
        }

        // ── Unflattening ─────────────────────────────────────────────────────────

        /// <summary>
        /// For a destination complex property (e.g. <c>Customer</c>), searches the
        /// flat source index for keys prefixed with its name (e.g. "CUSTOMERNAME",
        /// "CUSTOMEREMAIL") and builds the corresponding sub-object bindings.
        /// </summary>
        private static List<MemberBinding> BuildUnflattenBindings(
            Expression                       sourceParam,
            Dictionary<string, PropertyInfo> sourceIndex,
            string                           destPropName,
            Type                             subType)
        {
            var subBindings = new List<MemberBinding>();
            var prefix      = destPropName.ToUpperInvariant();

            foreach (var subProp in subType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!subProp.CanWrite) continue;

                // e.g. "CUSTOMER" + "NAME" = "CUSTOMERNAME"
                var flatKey = prefix + subProp.Name.ToUpperInvariant();
                if (!sourceIndex.TryGetValue(flatKey, out var sourceProp)) continue;

                Expression sourceAccess = Expression.Property(sourceParam, sourceProp);
                if (!TryAdaptExpression(sourceAccess, sourceProp.PropertyType, subProp.PropertyType, out var adapted))
                    continue;

                subBindings.Add(Expression.Bind(subProp, adapted!));
            }

            return subBindings;
        }

        // ── Type adaptation ──────────────────────────────────────────────────────

        /// <summary>
        /// Attempts to produce an <see cref="Expression"/> whose CLR type is exactly
        /// <paramref name="destType"/>, satisfying strict IL stack-parity rules and
        /// EF Core projection compatibility constraints.
        /// </summary>
        /// <remarks>
        /// <para><b>Nullable safety contract (Directive 1):</b></para>
        /// <list type="bullet">
        ///   <item>
        ///     <c>Nullable&lt;T&gt; → T</c>: emits
        ///     <c>source.HasValue ? source.Value : throw <see cref="MappingNullInvariantException"/></c>.
        ///     A bare <c>Expression.Convert</c> is never used — it generates an
        ///     <c>unbox.any</c> IL opcode that throws at runtime when the value is null.
        ///   </item>
        ///   <item>
        ///     <c>T → Nullable&lt;T&gt;</c>: <c>Expression.Convert</c> is safe here;
        ///     the CLR wraps the value type with no risk of null dereference.
        ///   </item>
        ///   <item>
        ///     <c>Nullable&lt;T&gt; → Nullable&lt;U&gt;</c>: emits a guarded ternary
        ///     that propagates null rather than producing bad IL.
        ///   </item>
        ///   <item>
        ///     <c>Nullable&lt;T&gt; → U</c> (numeric / enum, non-nullable dest):
        ///     same throw-on-null pattern — non-nullable destination is a domain invariant.
        ///   </item>
        /// </list>
        /// <para><b>Fail-fast contract (Directive 3):</b></para>
        /// Returns <see langword="false"/> when no safe conversion can be inferred;
        /// never silently returns <c>default</c> for a non-nullable destination that
        /// receives a null source.
        /// </remarks>
        private static bool TryAdaptExpression(
            Expression      sourceExpr,
            Type            sourceType,
            Type            destType,
            out Expression? result)
        {
            // ── Identical / directly assignable ──────────────────────────────────
            if (destType.IsAssignableFrom(sourceType))
            {
                result = sourceExpr;
                return true;
            }

            var sourceUnderlying = Nullable.GetUnderlyingType(sourceType);
            var destUnderlying   = Nullable.GetUnderlyingType(destType);
            var effectiveSource  = sourceUnderlying ?? sourceType;
            var effectiveDest    = destUnderlying   ?? destType;

            // ── Nullable<T> → T  ──────────────────────────────────────────────────
            // Directive 1: never emit a bare Convert — guard with HasValue.
            // Directive 3: non-nullable destination is a domain invariant — throw on null.
            if (sourceUnderlying is not null && destType == sourceUnderlying)
            {
                result = Expression.Condition(
                    Expression.Property(sourceExpr, nameof(Nullable<int>.HasValue)),
                    Expression.Property(sourceExpr, nameof(Nullable<int>.Value)),
                    BuildThrowInvariant(
                        $"Mapping invariant violated: Nullable<{sourceUnderlying.Name}> was null " +
                        $"when mapping to non-nullable '{destType.Name}'. " +
                        $"Change the destination to '{destType.Name}?' or use " +
                        $".ForMember(d => d.Prop, opt => opt.MapFrom(s => s.Prop ?? fallback)).",
                        destType));
                return true;
            }

            // ── T → Nullable<T>  (wrap — always safe) ────────────────────────────
            if (destUnderlying is not null && sourceType == destUnderlying)
            {
                result = Expression.Convert(sourceExpr, destType);
                return true;
            }

            // ── Nullable<T> → Nullable<U>  (cross-numeric, null propagated) ──────
            // Unwrap → convert underlying → re-wrap; null short-circuits to default.
            // A single Expression.Convert(Nullable<T>, Nullable<U>) produces bad IL.
            if (sourceUnderlying is not null && destUnderlying is not null
                && IsNumericType(sourceUnderlying) && IsNumericType(destUnderlying))
            {
                result = Expression.Condition(
                    Expression.Property(sourceExpr, nameof(Nullable<int>.HasValue)),
                    Expression.Convert(
                        Expression.Convert(
                            Expression.Property(sourceExpr, nameof(Nullable<int>.Value)),
                            destUnderlying),        // T  → U  (inner numeric convert)
                        destType),                  // U  → Nullable<U>  (re-wrap)
                    Expression.Default(destType));  // null propagates
                return true;
            }

            // ── Numeric widening / narrowing ──────────────────────────────────────
            if (IsNumericType(effectiveSource) && IsNumericType(effectiveDest))
            {
                // Nullable<T> → U (non-nullable): unwrap inner value, convert, throw on null.
                if (sourceUnderlying is not null && destUnderlying is null)
                {
                    result = Expression.Condition(
                        Expression.Property(sourceExpr, nameof(Nullable<int>.HasValue)),
                        Expression.Convert(
                            Expression.Property(sourceExpr, nameof(Nullable<int>.Value)),
                            destType),
                        BuildThrowInvariant(
                            $"Mapping invariant violated: Nullable<{sourceUnderlying.Name}> was null " +
                            $"when mapping to non-nullable numeric '{destType.Name}'. " +
                            $"Use ForMember to supply a fallback value.",
                            destType));
                    return true;
                }

                // T → U  |  T → Nullable<U>: Expression.Convert is safe for non-nullable source.
                result = Expression.Convert(sourceExpr, destType);
                return true;
            }

            // ── Enum ↔ integral ───────────────────────────────────────────────────
            // Delegate to BuildNullableAwareConvert which handles all four nullable
            // combinations (both non-null, src-null, dst-null, both-null).
            if ((effectiveSource.IsEnum && IsIntegralType(effectiveDest)) ||
                (effectiveDest.IsEnum  && IsIntegralType(effectiveSource)))
            {
                result = BuildNullableAwareConvert(
                    sourceExpr, sourceUnderlying, destType, destUnderlying);
                return result is not null;
            }

            // ── string → Enum (or Nullable<Enum>) ────────────────────────────────
            // Emits: source == null ? default(TDest) : Enum.Parse<TEnum>(source, true)
            // A null guard is required because Enum.Parse throws ArgumentNullException
            // on a null string. Empty strings are left to Enum.Parse so they surface
            // as ArgumentException (explicit failure per Directive 3).
            if (sourceType == typeof(string) && effectiveDest.IsEnum)
            {
                var parseMethod = typeof(Enum)
                    .GetMethod(nameof(Enum.Parse), genericParameterCount: 1,
                               types: [typeof(string), typeof(bool)])!
                    .MakeGenericMethod(effectiveDest);

                Expression parseCall = Expression.Call(
                    parseMethod, sourceExpr, Expression.Constant(true)); // ignoreCase

                Expression mappedValue = destUnderlying is not null
                    ? Expression.Convert(parseCall, destType)   // TEnum → Nullable<TEnum>
                    : parseCall;

                result = Expression.Condition(
                    Expression.Equal(sourceExpr, Expression.Constant(null, typeof(string))),
                    Expression.Default(destType),
                    mappedValue);
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        /// Emits a typed <see cref="Expression.Throw"/> node carrying a
        /// <see cref="MappingNullInvariantException"/> so both arms of a
        /// <c>Condition</c> share the same CLR result type, preserving IL stack parity.
        /// </summary>
        private static Expression BuildThrowInvariant(string message, Type resultType)
        {
            var ctor = typeof(MappingNullInvariantException)
                .GetConstructor([typeof(string)])!;
            return Expression.Throw(
                Expression.New(ctor, Expression.Constant(message)),
                resultType);
        }

        /// <summary>
        /// Builds a null-safe <c>Convert</c> expression for <b>enum ↔ integral</b>
        /// conversions, covering all four nullable-source / nullable-destination
        /// combinations:
        /// <list type="bullet">
        ///   <item><c>T → U</c> — direct <c>Expression.Convert</c> (always safe).</item>
        ///   <item><c>T → Nullable&lt;U&gt;</c> — direct convert (CLR wraps safely).</item>
        ///   <item>
        ///     <c>Nullable&lt;T&gt; → U</c> — guarded: <c>HasValue ? Convert(Value, U) : throw</c>.
        ///   </item>
        ///   <item>
        ///     <c>Nullable&lt;T&gt; → Nullable&lt;U&gt;</c> — guarded:
        ///     <c>HasValue ? Convert(Convert(Value, U), Nullable&lt;U&gt;) : default</c>.
        ///   </item>
        /// </list>
        /// </summary>
        private static Expression? BuildNullableAwareConvert(
            Expression sourceExpr,
            Type?      sourceUnderlying,
            Type       destType,
            Type?      destUnderlying)
        {
            // Non-nullable source: Expression.Convert is always safe.
            if (sourceUnderlying is null)
                return Expression.Convert(sourceExpr, destType);

            var hasValue   = Expression.Property(sourceExpr, nameof(Nullable<int>.HasValue));
            var innerValue = Expression.Property(sourceExpr, nameof(Nullable<int>.Value));

            if (destUnderlying is null)
            {
                // Nullable<T> → non-nullable: throw on null (Directive 3 — domain invariant).
                return Expression.Condition(
                    hasValue,
                    Expression.Convert(innerValue, destType),
                    BuildThrowInvariant(
                        $"Mapping invariant violated: Nullable<{sourceUnderlying.Name}> was null " +
                        $"when mapping to non-nullable enum/integral '{destType.Name}'. " +
                        $"Use ForMember to supply a fallback value.",
                        destType));
            }
            else
            {
                // Nullable<T> → Nullable<U>: propagate null, convert underlying value.
                return Expression.Condition(
                    hasValue,
                    Expression.Convert(
                        Expression.Convert(innerValue, destUnderlying),
                        destType),
                    Expression.Default(destType));
            }
        }

        // ── Source property index ────────────────────────────────────────────────

        /// <summary>
        /// Indexes all readable public instance properties of <paramref name="type"/>
        /// by their upper-invariant name for O(1) case-insensitive lookup.
        /// </summary>
        private static Dictionary<string, PropertyInfo> BuildSourceIndex(Type type)
        {
            var index = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // Skip indexers: they require arguments and cannot be used with
                // Expression.Property(expr, propertyInfo) without supplying those arguments.
                if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                    index[prop.Name.ToUpperInvariant()] = prop;
            }
            return index;
        }

        // ── Type classification sets ──────────────────────────────────────────────

        private static readonly HashSet<Type> NumericTypes =
        [
            typeof(byte),   typeof(sbyte),
            typeof(short),  typeof(ushort),
            typeof(int),    typeof(uint),
            typeof(long),   typeof(ulong),
            typeof(float),  typeof(double),
            typeof(decimal)
        ];

        private static readonly HashSet<Type> IntegralTypes =
        [
            typeof(byte),  typeof(sbyte),
            typeof(short), typeof(ushort),
            typeof(int),   typeof(uint),
            typeof(long),  typeof(ulong)
        ];

        private static bool IsNumericType(Type t)  => NumericTypes.Contains(t);
        private static bool IsIntegralType(Type t) => IntegralTypes.Contains(t);

        /// <summary>Returns <see langword="true"/> when <paramref name="t"/> can hold a null value (reference type or <c>Nullable&lt;T&gt;</c>).</summary>
        private static bool CanBeNull(Type t) => !t.IsValueType || Nullable.GetUnderlyingType(t) is not null;

        // ── ForMember lambda inlining ─────────────────────────────────────────────

        /// <summary>
        /// Replaces the single parameter of <paramref name="lambda"/> with
        /// <paramref name="sourceExpr"/> and returns the rewritten body expression.
        /// The lambda is never stored as a delegate — its body is inlined directly
        /// into the compiled mapping IL, incurring zero per-call overhead.
        /// </summary>
        private static Expression InlineLambdaBody(LambdaExpression lambda, Expression sourceExpr)
            => new ParameterReplacer(lambda.Parameters[0], sourceExpr).Visit(lambda.Body);

        /// <summary>
        /// Substitutes all occurrences of <see cref="_target"/> in an Expression
        /// Tree with <see cref="_replacement"/>.
        /// </summary>
        private sealed class ParameterReplacer : ExpressionVisitor
        {
            private readonly ParameterExpression _target;
            private readonly Expression          _replacement;

            internal ParameterReplacer(ParameterExpression target, Expression replacement)
            {
                _target      = target;
                _replacement = replacement;
            }

            protected override Expression VisitParameter(ParameterExpression node)
                => node == _target ? _replacement : base.VisitParameter(node);
        }

        // ── Configuration validation ─────────────────────────────────────────────

        /// <summary>
        /// Diagnostic entry point for <see cref="MapperConfiguration.AssertConfigurationIsValid"/>.
        /// Checks a registered pair for constructibility and for writable
        /// destination properties that convention matching (or an explicit
        /// <c>ForMember</c>) would leave unbound, recursing into nested
        /// complex-type property pairs (e.g. <c>Order.Customer → OrderDto.Customer</c>).
        /// </summary>
        internal static List<string> ValidatePair(Type sourceType, Type destType, MapperConfiguration configuration)
        {
            var errors  = new List<string>();
            var visited = new HashSet<(Type, Type)>();

            ValidateTopLevelPair(sourceType, destType, configuration, visited, errors);
            return errors;
        }

        private static void ValidateTopLevelPair(
            Type sourceType, Type destType, MapperConfiguration configuration,
            HashSet<(Type, Type)> visited, List<string> errors)
        {
            var pairLabel     = $"{sourceType.Name} -> {destType.Name}";
            var hasCustomCtor = configuration.TryGetCustomConstructor(sourceType, destType, out _);

            if (!hasCustomCtor && destType.GetConstructor(Type.EmptyTypes) is null)
            {
                errors.Add(
                    $"{pairLabel}: destination type '{destType.Name}' has no public parameterless " +
                    $"constructor and no ConstructUsing(...) is registered for this pair.");
                return; // construction itself is broken; member-level checks would be noise
            }

            // ConstructUsing is authoritative for this pair — convention matching
            // never runs, so there is nothing further to validate.
            if (hasCustomCtor)
                return;

            visited.Add((sourceType, destType));
            ValidateMembersInto(sourceType, destType, configuration, visited, errors, pairLabel);
        }

        /// <summary>
        /// Checks every writable destination property of <paramref name="destType"/>
        /// against convention matching (direct/flatten/unflatten) or an explicit
        /// <c>ForMember</c> override, recursing into nested complex-type pairs via
        /// <see cref="ValidateNestedComplexPair"/> when a name matches but the
        /// types aren't directly scalar-adaptable.
        /// </summary>
        private static void ValidateMembersInto(
            Type sourceType, Type destType, MapperConfiguration configuration,
            HashSet<(Type, Type)> visited, List<string> errors, string pairLabel)
        {
            var sourceIndex = BuildSourceIndex(sourceType);
            var sourceTrie  = GetOrBuildTrie(sourceType, configuration.GetFlattenDepth(sourceType, destType));
            var dummySource = Expression.Parameter(sourceType, "source");

            var destProps = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

            foreach (var destProp in destProps)
            {
                if (configuration.TryGetMemberOverride(sourceType, destType, destProp.Name, out _))
                    continue; // explicitly configured (Ignore or MapFrom) — always considered valid

                if (sourceIndex.TryGetValue(destProp.Name.ToUpperInvariant(), out var namedSrcProp))
                {
                    // Phase 1: direct scalar/enum/nullable match — reuse the real
                    // TryAdaptExpression against a throwaway (never-compiled) source
                    // expression to check actual type compatibility, not just name presence.
                    if (TryAdaptExpression(
                            Expression.Default(namedSrcProp.PropertyType), namedSrcProp.PropertyType,
                            destProp.PropertyType, out _))
                        continue;

                    // Phase 1 slow path: name matches but types aren't scalar-adaptable —
                    // only valid if it forms a proper nested-object mapping.
                    if (ValidateNestedComplexPair(
                            namedSrcProp.PropertyType, destProp.PropertyType, configuration, visited, errors,
                            $"{pairLabel}.{destProp.Name}"))
                        continue;

                    errors.Add(
                        $"{pairLabel}: destination property '{destProp.Name}' matches source property " +
                        $"'{namedSrcProp.Name}' by name, but '{namedSrcProp.PropertyType.Name}' does not " +
                        $"adapt to '{destProp.PropertyType.Name}' and does not form a valid nested mapping.");
                    continue;
                }

                if (TryBuildFlattenedSourcePath(dummySource, sourceTrie, destProp.Name, out _, out _))
                    continue; // Phase 2: flatten match

                var subType = destProp.PropertyType;
                if (!subType.IsValueType && subType != typeof(string)
                    && subType.GetConstructor(Type.EmptyTypes) is not null
                    && BuildUnflattenBindings(dummySource, sourceIndex, destProp.Name, subType).Count > 0)
                    continue; // Phase 3: unflatten match

                errors.Add(
                    $"{pairLabel}: destination property '{destProp.Name}' is not mapped by any " +
                    $"convention and has no ForMember(...) override. Add " +
                    $".ForMember(d => d.{destProp.Name}, opt => opt.Ignore()) if this is intentional.");
            }
        }

        /// <summary>
        /// Validates a nested complex-type pair discovered while matching a
        /// destination property by name (mirrors <see cref="TryBuildNestedObjectExpression"/>'s
        /// eligibility checks). Returns <see langword="false"/> when the pair isn't
        /// a valid nested-mapping target at all (not constructible); otherwise
        /// recurses into <see cref="ValidateMembersInto"/> (collecting errors under
        /// a dotted path, e.g. <c>Order -&gt; OrderDto.Customer</c>) and returns
        /// <see langword="true"/>. A pair already on the current validation path
        /// (self-referencing types, e.g. <c>Category.Parent</c>) is treated as
        /// already-valid rather than being re-validated or looping forever.
        /// </summary>
        private static bool ValidateNestedComplexPair(
            Type srcType, Type dstType, MapperConfiguration configuration,
            HashSet<(Type, Type)> visited, List<string> errors, string pairLabel)
        {
            if (srcType.IsValueType || srcType == typeof(string)) return false;
            if (dstType.IsValueType || dstType == typeof(string)) return false;

            var hasCustomCtor = configuration.TryGetCustomConstructor(srcType, dstType, out _);
            if (!hasCustomCtor && dstType.GetConstructor(Type.EmptyTypes) is null)
                return false; // not constructible — not a valid nested target

            if (hasCustomCtor)
                return true; // ConstructUsing authoritative — nothing further to check

            if (!visited.Add((srcType, dstType)))
                return true; // already validated (or currently being validated) elsewhere in this walk

            ValidateMembersInto(srcType, dstType, configuration, visited, errors, pairLabel);
            return true;
        }
    }
}
