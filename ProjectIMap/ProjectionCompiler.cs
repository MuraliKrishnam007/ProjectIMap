using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ProjectIMap
{
    /// <summary>
    /// Builds pure <see cref="Expression{TDelegate}"/> projection trees that EF Core
    /// can translate to optimised SQL SELECT statements.
    /// </summary>
    /// <remarks>
    /// <para><b>EF Core compatibility contract (Directive 2):</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     The body of every produced lambda is an <see cref="Expression.MemberInit"/>
    ///     whose bindings are exclusively <see cref="MemberAssignment"/> nodes
    ///     (<see cref="Expression.Bind"/>) — unless a <c>ConstructUsing</c>
    ///     expression is registered for the pair, in which case that expression
    ///     (itself expected to be pure) is inlined as-is.
    ///   </item>
    ///   <item>
    ///     <see cref="Expression.Block"/>, <see cref="Expression.Assign"/>,
    ///     local variable declarations, and try/catch nodes are <b>never emitted</b>.
    ///     EF Core's expression visitor cannot translate these constructs and will
    ///     throw <see cref="InvalidOperationException"/> at query execution time.
    ///   </item>
    ///   <item>
    ///     Navigation property chains (e.g. <c>src.Customer.Address.City</c>) are
    ///     emitted as nested <see cref="MemberExpression"/> nodes without null guards.
    ///     EF Core translates these into SQL LEFT JOINs automatically.
    ///   </item>
    ///   <item>
    ///     Nullable unwrapping uses <c>CASE WHEN HasValue THEN Value ELSE default</c>
    ///     rather than the <see cref="MappingNullInvariantException"/> throw path used
    ///     by the IL compiler, because <c>Expression.Throw</c> is not SQL-translatable.
    ///   </item>
    /// </list>
    /// </remarks>
    public static class ProjectionCompiler
    {
        // Projection lambdas are cached per type-pair PER CONFIGURATION. Keying by
        // the configuration (weakly, so a discarded config doesn't leak its cache)
        // matters because the same type pair can be registered with different
        // ForMember/ConvertUsing/depth settings in different configurations — a
        // single static (Type,Type) cache would leak one configuration's compiled
        // projection into another's queries.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            MapperConfiguration, ConcurrentDictionary<(Type, Type), LambdaExpression>> _caches = new();

        // Trie cache is separate from Mapper's: both are populated identically on
        // first access and then reused, so there is no correctness concern in having
        // two caches — only a one-time build cost per (source type, flatten depth).
        private static readonly ConcurrentDictionary<(Type, int), PropertyTrieNode> _trieCache = new();

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a cached <c>Expression&lt;Func&lt;TSource, TDest&gt;&gt;</c> projection
        /// lambda built from the registered mappings in <paramref name="config"/>.
        /// </summary>
        /// <remarks>
        /// The returned value is a <see cref="LambdaExpression"/> whose concrete CLR
        /// type is <c>Expression&lt;Func&lt;TSource, TDest&gt;&gt;</c>.  Callers may
        /// safely cast it using <see cref="Expression.Quote"/> or a direct type cast.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no mapping has been registered for
        /// <paramref name="source"/> → <paramref name="dest"/>.
        /// </exception>
        public static LambdaExpression BuildProjection(
            Type                source,
            Type                dest,
            MapperConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(dest);
            ArgumentNullException.ThrowIfNull(config);

            if (!config.IsRegistered(source, dest))
                throw new InvalidOperationException(
                    $"No mapping registered from '{source.Name}' to '{dest.Name}'. " +
                    $"Call CreateMap<{source.Name}, {dest.Name}>() in your MapperConfiguration " +
                    $"before calling ProjectTo.");

            var cache = _caches.GetOrCreateValue(config);
            return cache.GetOrAdd((source, dest), _ => BuildProjectionCore(source, dest, config));
        }

        // ── Core builder ─────────────────────────────────────────────────────────

        private static LambdaExpression BuildProjectionCore(
            Type                source,
            Type                dest,
            MapperConfiguration config)
        {
            var param = Expression.Parameter(source, "src");

            Expression body;
            if (config.TryGetCustomConstructor(source, dest, out var customCtor))
            {
                // ConstructUsing is authoritative — see Mapper.CompileMapping for the
                // equivalent runtime-engine rationale.
                body = InlineLambdaBody(customCtor!, param);
            }
            else
            {
                NewExpression newExpr;
                HashSet<string>? consumed = null;
                if (dest.GetConstructor(Type.EmptyTypes) is not null)
                {
                    newExpr = Expression.New(dest);
                }
                else
                {
                    // Constructor-parameter mapping (positional records / immutable
                    // types). EF Core translates NewExpression arguments in
                    // projections, so this stays SQL-compatible.
                    newExpr = TryBuildParameterizedProjectionConstructor(
                                  source, dest, param, config, out consumed)
                        ?? throw new InvalidOperationException(
                            $"Destination type '{dest.Name}' has no public parameterless constructor, " +
                            $"no ConstructUsing(...) registered for '{source.Name} -> {dest.Name}', " +
                            $"and no public constructor whose parameters all match source properties by name.");
                }

                var bindings = BuildProjectionBindings(source, dest, param, new Dictionary<(Type, Type), int>(), config);
                if (consumed is { Count: > 0 })
                    bindings.RemoveAll(b => consumed.Contains(b.Member.Name));

                body = Expression.MemberInit(newExpr, bindings);
            }

            // Produce Expression<Func<TSource, TDest>> (not a plain LambdaExpression)
            // so callers can safely cast and EF Core receives the correctly typed node.
            var delegateType = typeof(Func<,>).MakeGenericType(source, dest);
            return Expression.Lambda(delegateType, body, param);
        }

        // ── Projection bindings: 4-phase logic (Directive 2 compliant) ───────────

        /// <summary>
        /// Produces <see cref="MemberBinding"/> lists for a
        /// <paramref name="sourceType"/> → <paramref name="destType"/> pair.
        /// </summary>
        /// <remarks>
        /// Mirrors the four phases of <c>Mapper.BuildBindings</c> but emits only
        /// nodes that EF Core can translate to SQL:
        /// <list type="bullet">
        ///   <item>Phase 0 — ForMember overrides (Ignore / MapFrom / Condition / NullSubstitute)</item>
        ///   <item>Phase 1 — Direct case-insensitive name match</item>
        ///   <item>Phase 2 — Trie-based flattening via pure property chains</item>
        ///   <item>Phase 3 — Unflattening into destination complex types</item>
        /// </list>
        /// </remarks>
        private static List<MemberBinding> BuildProjectionBindings(
            Type                          sourceType,
            Type                          destType,
            Expression                    srcExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           config)
        {
            var pair     = (sourceType, destType);
            var maxDepth = config.GetMaxDepth(sourceType, destType);

            // Back-edge guard: identical contract to Mapper.BuildBindingPlans.
            visitedPath.TryGetValue(pair, out var currentDepth);
            if (currentDepth >= maxDepth)
                return [];

            visitedPath[pair] = currentDepth + 1;

            try
            {
                var sourceIndex    = BuildSourceIndex(sourceType);
                var sourceTrie     = GetOrBuildTrie(sourceType, config.GetFlattenDepth(sourceType, destType));
                var bindings       = new List<MemberBinding>();
                var boundDestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var destProps      = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                                            .ToArray();

                // ── Phase 0: ForMember overrides ─────────────────────────────────
                foreach (var destProp in destProps)
                {
                    if (!config.TryGetMemberOverride(
                            sourceType, destType, destProp.Name, out var mo)) continue;

                    boundDestNames.Add(destProp.Name);
                    if (mo!.IsIgnored) continue;

                    Expression? valueExpr = null;
                    Type?       valueType = null;

                    if (mo.MapFromExpression is not null)
                    {
                        valueExpr = InlineLambdaBody(mo.MapFromExpression, srcExpr);
                        valueType = valueExpr.Type;
                    }
                    else if ((mo.HasNullSubstitute || mo.ConditionExpression is not null)
                             && sourceIndex.TryGetValue(destProp.Name.ToUpperInvariant(), out var namedSrcProp))
                    {
                        valueExpr = Expression.Property(srcExpr, namedSrcProp);
                        valueType = namedSrcProp.PropertyType;
                    }

                    if (valueExpr is null) continue;

                    if (mo.HasNullSubstitute && CanBeNull(valueType!))
                    {
                        var substituteConst = Expression.Constant(mo.NullSubstituteValue, mo.NullSubstituteType!);

                        if (!TryAdaptProjectionExpression(
                                substituteConst, mo.NullSubstituteType!, valueType!, config, out var adaptedSubstitute))
                            adaptedSubstitute = substituteConst;

                        valueExpr = Expression.Condition(
                            Expression.Equal(valueExpr, Expression.Constant(null, valueType!)),
                            adaptedSubstitute!,
                            valueExpr);
                    }

                    if (!TryAdaptProjectionExpression(
                            valueExpr, valueType!, destProp.PropertyType, config, out var adapted)) continue;

                    // A per-member Condition is a CASE WHEN, exactly like the
                    // Mapper/MemberInit path — there is no "existing instance" concept
                    // in a SQL projection to skip an assignment on.
                    if (mo.ConditionExpression is not null)
                    {
                        var conditionBody = InlineLambdaBody(mo.ConditionExpression, srcExpr);
                        adapted = Expression.Condition(conditionBody, adapted!, Expression.Default(destProp.PropertyType));
                    }

                    bindings.Add(Expression.Bind(destProp, adapted!));
                }

                // ── Phase 1: Direct case-insensitive name match ───────────────────
                foreach (var destProp in destProps)
                {
                    if (boundDestNames.Contains(destProp.Name)) continue;
                    if (!sourceIndex.TryGetValue(
                            destProp.Name.ToUpperInvariant(), out var srcProp)) continue;

                    Expression srcAccess = Expression.Property(srcExpr, srcProp);

                    // Scalar / numeric / enum / nullable — fast path.
                    if (TryAdaptProjectionExpression(
                            srcAccess, srcProp.PropertyType, destProp.PropertyType, config, out var adapted))
                    {
                        bindings.Add(Expression.Bind(destProp, adapted!));
                        boundDestNames.Add(destProp.Name);
                        continue;
                    }

                    // Collection-typed member pair — a nested .Select(...) projection
                    // (SQL-translatable). Must run BEFORE nested-object recursion.
                    var nestedCollection = TryBuildCollectionProjection(
                        srcProp.PropertyType, destProp.PropertyType,
                        srcAccess, visitedPath, config);
                    if (nestedCollection is not null)
                    {
                        bindings.Add(Expression.Bind(destProp, nestedCollection));
                        boundDestNames.Add(destProp.Name);
                        continue;
                    }

                    // Complex type — recurse into sub-object (pure MemberInit, no Block).
                    var nested = TryBuildNestedProjection(
                        srcProp.PropertyType, destProp.PropertyType,
                        srcAccess, visitedPath, config);
                    if (nested is null) continue;

                    bindings.Add(Expression.Bind(destProp, nested));
                    boundDestNames.Add(destProp.Name);
                }

                // ── Phase 2: Trie-based flattening (pure property chains) ─────────
                foreach (var destProp in destProps)
                {
                    if (boundDestNames.Contains(destProp.Name)) continue;
                    if (!TryBuildFlattenedChain(
                            srcExpr, sourceTrie, destProp.Name,
                            out var flatExpr, out var flatType)) continue;
                    if (!TryAdaptProjectionExpression(
                            flatExpr!, flatType!, destProp.PropertyType, config, out var adapted)) continue;

                    bindings.Add(Expression.Bind(destProp, adapted!));
                    boundDestNames.Add(destProp.Name);
                }

                // ── Phase 3: Unflattening ─────────────────────────────────────────
                foreach (var destProp in destProps)
                {
                    if (boundDestNames.Contains(destProp.Name)) continue;

                    var subType = destProp.PropertyType;
                    if (subType.IsValueType || subType == typeof(string)) continue;
                    if (subType.GetConstructor(Type.EmptyTypes) is null)      continue;

                    var subBindings = BuildUnflattenProjectionBindings(
                        srcExpr, sourceIndex, config, destProp.Name, subType);
                    if (subBindings.Count == 0) continue;

                    bindings.Add(Expression.Bind(
                        destProp,
                        Expression.MemberInit(Expression.New(subType), subBindings)));
                    boundDestNames.Add(destProp.Name);
                }

                return bindings;
            }
            finally
            {
                var remaining = visitedPath[pair] - 1;
                if (remaining <= 0) visitedPath.Remove(pair);
                else visitedPath[pair] = remaining;
            }
        }

        /// <summary>
        /// Recurses into a complex source → destination pair and returns a bare
        /// <see cref="Expression.MemberInit"/> (or the inlined <c>ConstructUsing</c>
        /// expression, if registered) with no surrounding null guard.
        /// </summary>
        /// <remarks>
        /// EF Core translates navigation property access into SQL LEFT JOINs, so the
        /// null guard that the IL compiler needs is unnecessary here and would in fact
        /// prevent SQL translation if emitted as a <c>Block</c> node.
        /// </remarks>
        private static Expression? TryBuildNestedProjection(
            Type                          srcType,
            Type                          dstType,
            Expression                    srcExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           config)
        {
            if (srcType.IsValueType || srcType == typeof(string)) return null;
            if (dstType.IsValueType || dstType == typeof(string)) return null;

            var hasCustomCtor    = config.TryGetCustomConstructor(srcType, dstType, out var customCtor);
            var hasParameterless = dstType.GetConstructor(Type.EmptyTypes) is not null;

            if (hasCustomCtor)
                return InlineLambdaBody(customCtor!, srcExpr);

            var maxDepth = config.GetMaxDepth(srcType, dstType);
            visitedPath.TryGetValue((srcType, dstType), out var currentDepth);
            if (currentDepth >= maxDepth)
                return Expression.Constant(null, dstType);

            NewExpression? newExpr;
            HashSet<string>? consumed = null;
            if (hasParameterless)
            {
                newExpr = Expression.New(dstType);
            }
            else
            {
                newExpr = TryBuildParameterizedProjectionConstructor(
                    srcType, dstType, srcExpr, config, out consumed);
                if (newExpr is null) return null;   // not constructible — skip binding
            }

            var nestedBindings = BuildProjectionBindings(srcType, dstType, srcExpr, visitedPath, config);
            if (consumed is { Count: > 0 })
                nestedBindings.RemoveAll(b => consumed.Contains(b.Member.Name));
            if (nestedBindings.Count == 0 && newExpr.Arguments.Count == 0) return null;

            return Expression.MemberInit(newExpr, nestedBindings);
        }

        /// <summary>
        /// EF-safe mirror of the runtime engine's constructor-parameter mapping:
        /// matches every parameter of a public constructor to a source property by
        /// name (case-insensitive), adapting values with
        /// <see cref="TryAdaptProjectionExpression"/> so the resulting
        /// <see cref="NewExpression"/> stays SQL-translatable.
        /// </summary>
        private static NewExpression? TryBuildParameterizedProjectionConstructor(
            Type                sourceType,
            Type                destType,
            Expression          srcExpr,
            MapperConfiguration config,
            out HashSet<string> consumedNames)
        {
            consumedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceIndex = BuildSourceIndex(sourceType);

            foreach (var ctor in destType.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0) continue;

                var args    = new List<Expression>(parameters.Length);
                var matched = new List<string>(parameters.Length);
                var usable  = true;

                foreach (var parameter in parameters)
                {
                    if (parameter.Name is null ||
                        !sourceIndex.TryGetValue(parameter.Name.ToUpperInvariant(), out var sourceProp))
                    {
                        usable = false;
                        break;
                    }

                    Expression access = Expression.Property(srcExpr, sourceProp);
                    if (!TryAdaptProjectionExpression(
                            access, sourceProp.PropertyType, parameter.ParameterType, config, out var adapted))
                    {
                        usable = false;
                        break;
                    }

                    args.Add(adapted!);
                    matched.Add(parameter.Name);
                }

                if (!usable) continue;

                foreach (var name in matched)
                    consumedNames.Add(name);
                return Expression.New(ctor, args);
            }

            return null;
        }

        /// <summary>
        /// Builds a nested <c>.Select(...)</c> projection for a collection-typed
        /// member pair — e.g. <c>Order.Lines: List&lt;OrderLine&gt;</c> →
        /// <c>OrderDto.Lines: List&lt;OrderLineDto&gt;</c> becomes
        /// <c>src.Lines.Select(l =&gt; new OrderLineDto { … }).ToList()</c>, which EF
        /// Core translates to a correlated subquery. No null guard is emitted:
        /// relational providers materialize absent children as empty sets, and a
        /// guard would require a <c>Block</c>, which is not SQL-translatable.
        /// </summary>
        private static Expression? TryBuildCollectionProjection(
            Type                          srcPropType,
            Type                          dstPropType,
            Expression                    srcExpr,
            Dictionary<(Type, Type), int> visitedPath,
            MapperConfiguration           config)
        {
            if (srcPropType == typeof(string) || dstPropType == typeof(string)) return null;
            if (srcPropType.IsValueType || dstPropType.IsValueType) return null;
            if (!Mapper.TryGetCollectionElementType(srcPropType, out var srcElem) ||
                !Mapper.TryGetCollectionElementType(dstPropType, out var dstElem)) return null;

            var iEnumerableSrc = typeof(IEnumerable<>).MakeGenericType(srcElem);
            var funcType       = typeof(Func<,>).MakeGenericType(srcElem, dstElem);

            // ── Per-element projection strategy ──────────────────────────────────
            LambdaExpression? selector = null;   // null ⇒ identity (no Select emitted)
            if (srcElem != dstElem)
            {
                var elemParam = Expression.Parameter(srcElem, "elem");
                if (TryAdaptProjectionExpression(elemParam, srcElem, dstElem, config, out var adaptedElem))
                {
                    selector = Expression.Lambda(funcType, adaptedElem!, elemParam);
                }
                else if (!srcElem.IsValueType && !dstElem.IsValueType)
                {
                    var elemInit = TryBuildNestedProjection(srcElem, dstElem, elemParam, visitedPath, config);
                    if (elemInit is null) return null;
                    selector = Expression.Lambda(funcType, elemInit, elemParam);
                }
                else
                {
                    return null;
                }
            }

            Expression seq = srcPropType == iEnumerableSrc
                ? srcExpr
                : Expression.Convert(srcExpr, iEnumerableSrc);

            if (selector is not null)
            {
                var selectMethod = typeof(Enumerable)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == nameof(Enumerable.Select)
                             && m.GetParameters() is { Length: 2 } ps
                             && ps[1].ParameterType.IsGenericType
                             && ps[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>))
                    .MakeGenericMethod(srcElem, dstElem);
                seq = Expression.Call(selectMethod, seq, selector);
            }

            // ── Materialize (ToList / ToArray only — both EF-translatable) ────────
            if (dstPropType.IsArray)
            {
                var toArray = typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(dstElem);
                return Expression.Call(toArray, seq);
            }

            if (dstPropType.IsAssignableFrom(typeof(List<>).MakeGenericType(dstElem)))
            {
                var toList = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(dstElem);
                Expression materialized = Expression.Call(toList, seq);
                if (materialized.Type != dstPropType)
                    materialized = Expression.Convert(materialized, dstPropType);
                return materialized;
            }

            return null;
        }

        // ── Unflattening helper ──────────────────────────────────────────────────

        private static List<MemberBinding> BuildUnflattenProjectionBindings(
            Expression                       srcExpr,
            Dictionary<string, PropertyInfo> sourceIndex,
            MapperConfiguration              config,
            string                           destPropName,
            Type                             subType)
        {
            var subBindings = new List<MemberBinding>();
            var prefix      = destPropName.ToUpperInvariant();

            foreach (var subProp in subType.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (!subProp.CanWrite) continue;

                var flatKey = prefix + subProp.Name.ToUpperInvariant();
                if (!sourceIndex.TryGetValue(flatKey, out var srcProp)) continue;

                Expression srcAccess = Expression.Property(srcExpr, srcProp);
                if (!TryAdaptProjectionExpression(
                        srcAccess, srcProp.PropertyType, subProp.PropertyType, config, out var adapted))
                    continue;

                subBindings.Add(Expression.Bind(subProp, adapted!));
            }

            return subBindings;
        }

        // ── Pure property-chain trie traversal ───────────────────────────────────

        /// <summary>
        /// Resolves a flat destination name (e.g. <c>"CustomerName"</c>) to a direct
        /// property chain on the source (e.g. <c>src.Customer.Name</c>) by walking
        /// the pre-built <see cref="PropertyTrieNode"/> trie.
        /// </summary>
        /// <remarks>
        /// Unlike <c>Mapper.TryTraverseTrie</c>, this method emits no
        /// <c>Expression.Block</c>, no temp variables, and no null guards.
        /// The resulting pure <c>MemberExpression</c> chain is what EF Core expects
        /// for navigation property traversal.
        /// </remarks>
        private static bool TryBuildFlattenedChain(
            Expression       srcExpr,
            PropertyTrieNode trieRoot,
            string           destName,
            out Expression?  result,
            out Type?        resultType)
            => TryTraversePureChain(
                srcExpr, trieRoot, destName.AsSpan(),
                isTopLevel: true, out result, out resultType);

        private static bool TryTraversePureChain(
            Expression         currentExpr,
            PropertyTrieNode   currentNode,
            ReadOnlySpan<char> remaining,
            bool               isTopLevel,
            out Expression?    result,
            out Type?          resultType)
        {
            foreach (var (key, childNode) in currentNode.Children)
            {
                if (remaining.Length < key.Length) continue;
                if (!remaining[..key.Length].Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var prop          = childNode.Property!;
                var nextRemaining = remaining[key.Length..];

                // Phase 1 owns exact single-segment matches at the top level; skip them.
                if (isTopLevel && nextRemaining.IsEmpty) continue;

                if (nextRemaining.IsEmpty)
                {
                    // ── Leaf: the entire destination name has been consumed ────────
                    result     = Expression.Property(currentExpr, prop);
                    resultType = prop.PropertyType;
                    return true;
                }

                // ── Non-leaf: step one segment deeper using a pure property access.
                // No Block, no variable — just chain the MemberExpression directly.
                // EF Core interprets this as a navigation property join.
                var nextExpr = Expression.Property(currentExpr, prop);

                if (!TryTraversePureChain(
                        nextExpr, childNode, nextRemaining,
                        isTopLevel: false, out result, out resultType))
                    continue;

                return true;
            }

            result     = null;
            resultType = null;
            return false;
        }

        // ── EF Core–safe type adaptation ────────────────────────────────────────

        /// <summary>
        /// Produces an expression whose CLR type is exactly <paramref name="destType"/>,
        /// using only nodes that EF Core can translate to SQL.
        /// </summary>
        /// <remarks>
        /// Key differences from <c>Mapper.TryAdaptExpression</c>:
        /// <list type="bullet">
        ///   <item>
        ///     <c>Nullable&lt;T&gt; → T</c>: emits
        ///     <c>HasValue ? Value : default(T)</c> instead of throwing
        ///     <see cref="MappingNullInvariantException"/>. EF Core translates this to
        ///     <c>CASE WHEN col IS NOT NULL THEN col ELSE 0/null END</c>.
        ///   </item>
        ///   <item>
        ///     <c>Expression.Throw</c> is <b>never emitted</b> — EF Core cannot
        ///     translate CLR exception construction to SQL.
        ///   </item>
        /// </list>
        /// </remarks>
        private static bool TryAdaptProjectionExpression(
            Expression          srcExpr,
            Type                sourceType,
            Type                destType,
            MapperConfiguration config,
            out Expression?     result)
        {
            // ── Global ConvertUsing converter (precedence over every built-in rule) ─
            // The user lambda is inlined, so it remains SQL-translatable if its body
            // is. Null sources propagate as default via a ternary (CASE WHEN in SQL)
            // instead of reaching the lambda — same contract as the runtime engine.
            if (config.TryGetTypeConverter(sourceType, destType, out var converter))
            {
                var inlined = InlineLambdaBody(converter!, srcExpr);
                result = CanBeNull(sourceType)
                    ? Expression.Condition(
                          Expression.Equal(srcExpr, Expression.Constant(null, sourceType)),
                          Expression.Default(destType),
                          inlined)
                    : inlined;
                return true;
            }

            // ── Identical / directly assignable ──────────────────────────────────
            if (destType.IsAssignableFrom(sourceType))
            {
                result = srcExpr;
                return true;
            }

            var srcUnderlying  = Nullable.GetUnderlyingType(sourceType);
            var destUnderlying = Nullable.GetUnderlyingType(destType);
            var effectiveSrc   = srcUnderlying  ?? sourceType;
            var effectiveDest  = destUnderlying ?? destType;

            // ── Nullable<T> → T ───────────────────────────────────────────────────
            // EF Core translates: CASE WHEN src IS NOT NULL THEN src ELSE default END
            if (srcUnderlying is not null && destType == srcUnderlying)
            {
                result = Expression.Condition(
                    Expression.Property(srcExpr, nameof(Nullable<int>.HasValue)),
                    Expression.Property(srcExpr, nameof(Nullable<int>.Value)),
                    Expression.Default(destType));
                return true;
            }

            // ── T → Nullable<T> (wrap — always safe) ─────────────────────────────
            if (destUnderlying is not null && sourceType == destUnderlying)
            {
                result = Expression.Convert(srcExpr, destType);
                return true;
            }

            // ── Nullable<T> → Nullable<U> (cross-numeric) ────────────────────────
            if (srcUnderlying is not null && destUnderlying is not null
                && IsNumericType(srcUnderlying) && IsNumericType(destUnderlying))
            {
                result = Expression.Condition(
                    Expression.Property(srcExpr, nameof(Nullable<int>.HasValue)),
                    Expression.Convert(
                        Expression.Convert(
                            Expression.Property(srcExpr, nameof(Nullable<int>.Value)),
                            destUnderlying),
                        destType),
                    Expression.Default(destType));
                return true;
            }

            // ── Numeric widening / narrowing ──────────────────────────────────────
            if (IsNumericType(effectiveSrc) && IsNumericType(effectiveDest))
            {
                if (srcUnderlying is not null && destUnderlying is null)
                {
                    // Nullable<T> → U: default(U) on null — EF translates to COALESCE / CAST
                    result = Expression.Condition(
                        Expression.Property(srcExpr, nameof(Nullable<int>.HasValue)),
                        Expression.Convert(
                            Expression.Property(srcExpr, nameof(Nullable<int>.Value)),
                            destType),
                        Expression.Default(destType));
                    return true;
                }

                result = Expression.Convert(srcExpr, destType);
                return true;
            }

            // ── Enum ↔ integral ───────────────────────────────────────────────────
            if ((effectiveSrc.IsEnum  && IsIntegralType(effectiveDest)) ||
                (effectiveDest.IsEnum && IsIntegralType(effectiveSrc)))
            {
                result = BuildProjectionNullableConvert(srcExpr, srcUnderlying, destType, destUnderlying);
                return result is not null;
            }

            // ── string → Enum (or Nullable<Enum>) ────────────────────────────────
            // EF Core will evaluate this client-side if the source column is a string.
            // A null guard is still required to avoid ArgumentNullException on null strings.
            if (sourceType == typeof(string) && effectiveDest.IsEnum)
            {
                var parseMethod = typeof(Enum)
                    .GetMethod(nameof(Enum.Parse), genericParameterCount: 1,
                               types: [typeof(string), typeof(bool)])!
                    .MakeGenericMethod(effectiveDest);

                Expression parseCall  = Expression.Call(parseMethod, srcExpr, Expression.Constant(true));
                Expression mappedValue = destUnderlying is not null
                    ? Expression.Convert(parseCall, destType)
                    : parseCall;

                result = Expression.Condition(
                    Expression.Equal(srcExpr, Expression.Constant(null, typeof(string))),
                    Expression.Default(destType),
                    mappedValue);
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        /// EF Core–safe nullable convert for enum ↔ integral.
        /// Uses <see cref="Expression.Default"/> on the null branch instead of
        /// <see cref="MappingNullInvariantException"/> so the expression remains
        /// SQL-translatable.
        /// </summary>
        private static Expression? BuildProjectionNullableConvert(
            Expression srcExpr,
            Type?      srcUnderlying,
            Type       destType,
            Type?      destUnderlying)
        {
            // Non-nullable source: direct convert is always safe.
            if (srcUnderlying is null)
                return Expression.Convert(srcExpr, destType);

            var hasValue   = Expression.Property(srcExpr, nameof(Nullable<int>.HasValue));
            var innerValue = Expression.Property(srcExpr, nameof(Nullable<int>.Value));

            if (destUnderlying is null)
            {
                // Nullable<T> → U: default(U) on null — EF: CASE WHEN ... THEN CAST(...) ELSE 0
                return Expression.Condition(
                    hasValue,
                    Expression.Convert(innerValue, destType),
                    Expression.Default(destType));
            }

            // Nullable<T> → Nullable<U>: propagate null, convert underlying value.
            return Expression.Condition(
                hasValue,
                Expression.Convert(Expression.Convert(innerValue, destUnderlying), destType),
                Expression.Default(destType));
        }

        // ── Trie construction ─────────────────────────────────────────────────────

        private static PropertyTrieNode GetOrBuildTrie(Type sourceType, int depth)
            => _trieCache.GetOrAdd((sourceType, depth), static key =>
            {
                var root = new PropertyTrieNode();
                PopulateTrie(root, key.Item1, depthRemaining: key.Item2, visitedOnPath: [key.Item1]);
                return root;
            });

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
                if (!propType.IsValueType
                    && propType != typeof(string)
                    && !propType.IsArray
                    && !propType.IsGenericType
                    && visitedOnPath.Add(propType))
                {
                    PopulateTrie(child, propType, depthRemaining - 1, visitedOnPath);
                    visitedOnPath.Remove(propType);
                }
            }
        }

        // ── Source property index ─────────────────────────────────────────────────

        private static Dictionary<string, PropertyInfo> BuildSourceIndex(Type type)
        {
            var index = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                    index[prop.Name.ToUpperInvariant()] = prop;
            }
            return index;
        }

        // ── Type classification sets ──────────────────────────────────────────────

        private static readonly HashSet<Type> _numericTypes =
        [
            typeof(byte),   typeof(sbyte),
            typeof(short),  typeof(ushort),
            typeof(int),    typeof(uint),
            typeof(long),   typeof(ulong),
            typeof(float),  typeof(double),
            typeof(decimal)
        ];

        private static readonly HashSet<Type> _integralTypes =
        [
            typeof(byte),  typeof(sbyte),
            typeof(short), typeof(ushort),
            typeof(int),   typeof(uint),
            typeof(long),  typeof(ulong)
        ];

        private static bool IsNumericType(Type t)  => _numericTypes.Contains(t);
        private static bool IsIntegralType(Type t) => _integralTypes.Contains(t);

        /// <summary>Returns <see langword="true"/> when <paramref name="t"/> can hold a null value (reference type or <c>Nullable&lt;T&gt;</c>).</summary>
        private static bool CanBeNull(Type t) => !t.IsValueType || Nullable.GetUnderlyingType(t) is not null;

        // ── ForMember lambda inlining ─────────────────────────────────────────────

        /// <summary>
        /// Replaces the single parameter of <paramref name="lambda"/> with
        /// <paramref name="srcExpr"/> and returns the rewritten body.
        /// The lambda body is inlined directly into the projection tree so EF Core
        /// sees the raw member-access chain rather than a delegate invocation.
        /// </summary>
        private static Expression InlineLambdaBody(LambdaExpression lambda, Expression srcExpr)
            => new ParameterReplacer(lambda.Parameters[0], srcExpr).Visit(lambda.Body);

        private sealed class ParameterReplacer(ParameterExpression target, Expression replacement)
            : ExpressionVisitor
        {
            protected override Expression VisitParameter(ParameterExpression node)
                => node == target ? replacement : base.VisitParameter(node);
        }
    }
}
