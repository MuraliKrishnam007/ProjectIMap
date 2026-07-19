using System.Collections.Generic;
using System.Reflection;

namespace ProjectIMap
{
    /// <summary>
    /// A single node in the property-path trie built over a source type's object graph.
    /// </summary>
    /// <remarks>
    /// Each node sits at a segment boundary.  The root has no <see cref="Property"/>
    /// (it represents the source object itself); every other node holds the
    /// <see cref="PropertyInfo"/> for the segment that leads to it.
    ///
    /// Children are keyed by the <em>exact</em> property name of the next segment and
    /// looked up case-insensitively, so a destination property named
    /// <c>CustomerName</c> can be resolved to <c>source.Customer.Name</c> regardless
    /// of casing conventions.
    /// </remarks>
    internal sealed class PropertyTrieNode
    {
        /// <summary>
        /// The property this node represents as a segment endpoint.
        /// <see langword="null"/> only on the root node.
        /// </summary>
        public PropertyInfo? Property { get; init; }

        /// <summary>
        /// Child nodes keyed by their property-name segment.
        /// Lookups are case-insensitive (<see cref="StringComparer.OrdinalIgnoreCase"/>).
        /// </summary>
        public Dictionary<string, PropertyTrieNode> Children { get; }
            = new(StringComparer.OrdinalIgnoreCase);
    }
}
