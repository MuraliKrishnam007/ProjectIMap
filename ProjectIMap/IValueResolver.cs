namespace ProjectIMap
{
    /// <summary>
    /// A reusable, named alternative to an inline <c>MapFrom</c> lambda for
    /// computing a single destination member's value.
    /// </summary>
    /// <typeparam name="TSource">The mapping's source type.</typeparam>
    /// <typeparam name="TMember">The type of value this resolver produces.</typeparam>
    /// <remarks>
    /// Register an instance via <c>IMemberConfigurationExpression{TSource}.MapFrom{TMember}(IValueResolver{TSource,TMember})</c>.
    /// Implementations may take constructor dependencies — the caller constructs
    /// the instance, so there is no dependency-injection wiring inside this library.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class FullNameResolver : IValueResolver&lt;Employee, string&gt;
    /// {
    ///     public string Resolve(Employee source) =&gt; $"{source.FirstName} {source.LastName}";
    /// }
    ///
    /// config.CreateMap&lt;Employee, EmployeeDto&gt;()
    ///       .ForMember(d =&gt; d.FullName, opt =&gt; opt.MapFrom(new FullNameResolver()));
    /// </code>
    /// </example>
    public interface IValueResolver<in TSource, out TMember>
    {
        /// <summary>Computes the destination member's value from <paramref name="source"/>.</summary>
        TMember Resolve(TSource source);
    }
}
