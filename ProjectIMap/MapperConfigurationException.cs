using System;

namespace ProjectIMap
{
    /// <summary>
    /// Thrown by <see cref="MapperConfiguration.AssertConfigurationIsValid"/> when
    /// one or more registered type pairs fail validation (missing constructor,
    /// or a writable destination property with no matching source and no
    /// explicit <c>ForMember</c> override).
    /// </summary>
    public sealed class MapperConfigurationException : InvalidOperationException
    {
        /// <inheritdoc cref="InvalidOperationException(string)"/>
        public MapperConfigurationException(string message)
            : base(message) { }

        /// <inheritdoc cref="InvalidOperationException(string, Exception)"/>
        public MapperConfigurationException(string message, Exception inner)
            : base(message, inner) { }
    }
}
