using System;

namespace ProjectIMap
{
    /// <summary>
    /// Thrown at runtime when the IL mapping compiler encounters a null value on a
    /// <c>Nullable&lt;T&gt;</c> source expression that is being mapped to a
    /// <em>non-nullable</em> destination property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exception is emitted directly into the compiled Expression Tree as an
    /// <c>Expression.Throw</c> node so that the CLR evaluation stack remains
    /// well-typed at all times (both arms of the guarding
    /// <c>Expression.Condition</c> carry the same result type).
    /// </para>
    /// <para>
    /// <b>Resolution options</b>:
    /// <list type="bullet">
    ///   <item>
    ///     Change the destination property to <c>T?</c> so null propagates safely.
    ///   </item>
    ///   <item>
    ///     Use <c>.ForMember(d =&gt; d.Prop, opt =&gt; opt.MapFrom(s =&gt; s.Nullable ?? fallback))</c>
    ///     to supply an explicit default in the mapping configuration.
    ///   </item>
    /// </list>
    /// </para>
    /// </remarks>
    public sealed class MappingNullInvariantException : InvalidOperationException
    {
        /// <inheritdoc cref="InvalidOperationException(string)"/>
        public MappingNullInvariantException(string message)
            : base(message) { }

        /// <inheritdoc cref="InvalidOperationException(string, Exception)"/>
        public MappingNullInvariantException(string message, Exception inner)
            : base(message, inner) { }
    }
}
