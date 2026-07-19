using System;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Provides <see cref="IServiceCollection"/> extension methods for registering
    /// the ProjectIMap mapping engine with a dependency-injection container.
    /// </summary>
    public static class MapperServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the ProjectIMap mapping engine as a <b>Singleton</b> service
        /// pair — <see cref="ProjectIMap.MapperConfiguration"/> and
        /// <see cref="ProjectIMap.IMapper"/> — and invokes
        /// <paramref name="configAction"/> immediately to populate the configuration.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <param name="configAction">
        /// A delegate that receives the <see cref="ProjectIMap.MapperConfiguration"/>
        /// instance and registers type-pair mappings via
        /// <see cref="ProjectIMap.MapperConfiguration.CreateMap{TSource,TDestination}"/>.
        /// This delegate is executed <b>once at registration time</b>, not on every
        /// resolve, so it does not affect request-time performance.
        /// </param>
        /// <returns>
        /// The original <paramref name="services"/> instance to allow method chaining.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Both <see cref="ProjectIMap.MapperConfiguration"/> and
        /// <see cref="ProjectIMap.IMapper"/> are registered as <b>Singletons</b>
        /// because the mapping engine is fully thread-safe:
        /// </para>
        /// <list type="bullet">
        ///   <item>
        ///     <see cref="ProjectIMap.MapperConfiguration"/> stores registered type pairs
        ///     in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
        ///     and is immutable after <paramref name="configAction"/> returns.
        ///   </item>
        ///   <item>
        ///     <see cref="ProjectIMap.Mapper"/> caches compiled
        ///     <see cref="System.Linq.Expressions.Expression"/> Tree delegates in a
        ///     second <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.
        ///     The first call per type pair incurs the one-time JIT compilation cost;
        ///     every subsequent call executes the cached native-IL delegate at
        ///     near-zero overhead.
        ///   </item>
        /// </list>
        /// <para>
        /// <b>Example — ASP.NET Core / generic host setup:</b>
        /// </para>
        /// <code>
        /// builder.Services.AddMyCustomMapper(cfg =>
        /// {
        ///     cfg.CreateMap&lt;Order, OrderDto&gt;().ReverseMap();
        ///     cfg.CreateMap&lt;Customer, CustomerDto&gt;().ReverseMap();
        /// });
        /// </code>
        /// <para>
        /// Inject <see cref="ProjectIMap.IMapper"/> into any constructor as usual:
        /// </para>
        /// <code>
        /// public class OrderService(IMapper mapper) { … }
        /// </code>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/> or <paramref name="configAction"/>
        /// is <see langword="null"/>.
        /// </exception>
        public static IServiceCollection AddMyCustomMapper(
            this IServiceCollection                    services,
            Action<ProjectIMap.MapperConfiguration>    configAction)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configAction);

            var configuration = new ProjectIMap.MapperConfiguration();
            configAction(configuration);

            services.AddSingleton(configuration);
            services.AddSingleton<ProjectIMap.IMapper, ProjectIMap.Mapper>();

            return services;
        }

        /// <summary>
        /// Scans <paramref name="assembliesToScan"/> for concrete
        /// <see cref="ProjectIMap.MappingProfile"/> subclasses, instantiates each one,
        /// merges their mapping rules into a single
        /// <see cref="ProjectIMap.MapperConfiguration"/>, and registers both the
        /// configuration and <see cref="ProjectIMap.IMapper"/> as <b>Singletons</b>.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <param name="assembliesToScan">
        /// One or more assemblies to search for <see cref="ProjectIMap.MappingProfile"/>
        /// subclasses.  Pass <c>Assembly.GetExecutingAssembly()</c> or
        /// <c>typeof(MyProfile).Assembly</c> to target specific assemblies.
        /// Duplicate assemblies are scanned only once.
        /// </param>
        /// <returns>
        /// The original <paramref name="services"/> instance to allow method chaining.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>Discovery rules</b> — a type is loaded as a profile when all of the
        /// following hold:
        /// </para>
        /// <list type="number">
        ///   <item>It is a <c>class</c> (not an interface or struct).</item>
        ///   <item>It is not <see langword="abstract"/>.</item>
        ///   <item>It inherits (directly or indirectly) from <see cref="ProjectIMap.MappingProfile"/>.</item>
        ///   <item>It has a public parameterless constructor.</item>
        /// </list>
        /// <para>
        /// Each matching type is instantiated with
        /// <see cref="Activator.CreateInstance(Type)"/>, which executes its
        /// constructor.  Mapping registrations made inside that constructor
        /// (via the protected <c>CreateMap</c> method) are then transferred to the
        /// global <see cref="ProjectIMap.MapperConfiguration"/> via
        /// <c>MappingProfile.ApplyTo</c> — a non-generic, allocation-light copy that
        /// involves no additional reflection after the constructor returns.
        /// </para>
        /// <para>
        /// <b>Example — ASP.NET Core / generic host setup:</b>
        /// </para>
        /// <code>
        /// // In Program.cs:
        /// builder.Services.AddMyMapper(typeof(OrderProfile).Assembly);
        ///
        /// // Profile definition (discovered automatically):
        /// public class OrderProfile : MappingProfile
        /// {
        ///     public OrderProfile()
        ///     {
        ///         CreateMap&lt;Order, OrderDto&gt;().ReverseMap();
        ///         CreateMap&lt;OrderLine, OrderLineDto&gt;();
        ///     }
        /// }
        ///
        /// // Constructor injection — no further setup needed:
        /// public class OrderService(IMapper mapper) { … }
        /// </code>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="assembliesToScan"/> is empty.
        /// </exception>
        public static IServiceCollection AddMyMapper(
            this IServiceCollection services,
            params Assembly[]       assembliesToScan)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (assembliesToScan is null || assembliesToScan.Length == 0)
                throw new ArgumentException(
                    "At least one assembly must be provided.", nameof(assembliesToScan));

            var profileBaseType   = typeof(ProjectIMap.MappingProfile);
            var configuration     = new ProjectIMap.MapperConfiguration();
            var scannedAssemblies = new System.Collections.Generic.HashSet<Assembly>();

            foreach (var assembly in assembliesToScan)
            {
                // Silently skip null entries and already-scanned assemblies.
                if (assembly is null || !scannedAssemblies.Add(assembly))
                    continue;

                foreach (var type in assembly.GetTypes())
                {
                    // Must be a concrete, non-abstract class that extends MappingProfile.
                    if (!type.IsClass || type.IsAbstract)
                        continue;

                    if (!profileBaseType.IsAssignableFrom(type))
                        continue;

                    // Require a public parameterless constructor so Activator can
                    // instantiate the profile without needing a DI container.
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    // Instantiate: the profile constructor runs CreateMap calls,
                    // populating its private inner MapperConfiguration.
                    var profile = (ProjectIMap.MappingProfile)Activator.CreateInstance(type)!;

                    // Merge this profile's registrations into the global configuration.
                    // ReverseMap() pairs are already recorded inside the profile —
                    // ApplyTo transfers everything in a single non-generic loop.
                    profile.ApplyTo(configuration);
                }
            }

            // Register the fully-populated configuration as a singleton instance.
            // No factory lambda: the object is already built, so resolving it is
            // a direct field read inside the DI container.
            services.AddSingleton(configuration);

            // Mapper receives MapperConfiguration via constructor injection and
            // lazily compiles Expression Tree delegates on first Map<,>() call.
            services.AddSingleton<ProjectIMap.IMapper, ProjectIMap.Mapper>();

            return services;
        }
    }
}
