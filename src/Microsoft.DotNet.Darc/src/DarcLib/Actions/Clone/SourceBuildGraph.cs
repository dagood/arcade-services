// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DotNet.DarcLib.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.DotNet.DarcLib.Actions.Clone
{
    public class SourceBuildGraph
    {
        public static SourceBuildGraph CreateAndAddMissingLeafNodes(IEnumerable<SourceBuildNode> nodes)
        {
            var graph = new SourceBuildGraph(nodes);
            graph.AddMissingLeafNodes();
            return graph;
        }

        public SourceBuildGraph(IEnumerable<SourceBuildNode> nodes)
        {
            SetNodes(nodes.ToArray());
        }

        public IReadOnlyList<SourceBuildNode> Nodes { get; set; }

        public IEnumerable<SourceBuildEdge> AllEdges => Nodes.SelectMany(n => n.UpstreamEdges.NullAsEmpty());

        public Dictionary<SourceBuildIdentity, SourceBuildNode> IdentityNodes { get; set; }

        /// <summary>
        /// Keep a fast lookup of all edges that have a specified identity as its downstream. You
        /// can get from a node to all of its upstream edges, and this allows the opposite.
        /// </summary>
        public Dictionary<SourceBuildIdentity, SourceBuildEdge[]> EdgesWithUpstream { get; set; }

        /// <summary>
        /// Some identities were intentionally unexplored. Keep track here, to display later if
        /// necessary for diagnostics.
        /// </summary>
        public IReadOnlyList<SourceBuildNode> IdentitiesWithoutNodes { get; set; }

        /// <summary>
        /// Create sentinel nodes for identities that have no nodes. This allows for easier graph
        /// traversal but makes it harder to spot partially completed nodes during graph buildup.
        /// </summary>
        public void AddMissingLeafNodes()
        {
            // Some nodes have upstreams that we can't find nodes for.
            IdentitiesWithoutNodes = Nodes
                .SelectMany(
                    n => n.UpstreamEdges.NullAsEmpty()
                        .Where(u => !IdentityNodes.ContainsKey(u.Upstream))
                        .Select(u => new SourceBuildNode
                        {
                            Identity = u.Upstream,
                            UpstreamEdges = Enumerable.Empty<SourceBuildEdge>()
                        }))
                .Distinct(SourceBuildNode.CaseInsensitiveComparer)
                .ToArray();

            if (IdentitiesWithoutNodes.Any())
            {
                // Generate sentinel values for identities without nodes to make graph operations simpler.
                SetNodes(Nodes.Concat(IdentitiesWithoutNodes).ToArray());
            }
        }

        private void SetNodes(IReadOnlyList<SourceBuildNode> nodes)
        {
            Nodes = nodes;

            IdentityNodes = Nodes.ToDictionary(
                n => n.Identity,
                n => n,
                SourceBuildIdentity.CaseInsensitiveComparer);

            EdgesWithUpstream = Nodes
                .SelectMany(n => n.UpstreamEdges.NullAsEmpty())
                .GroupBy(e => e.Upstream, SourceBuildIdentity.CaseInsensitiveComparer)
                .ToDictionary(
                    e => e.Key,
                    e => e.ToArray(),
                    SourceBuildIdentity.CaseInsensitiveComparer);
        }

        public string ToGraphVizString(
            bool includeLegend = true,
            bool includeCoherencyRedirects = true)
        {
            var sb = new StringBuilder("digraph G {\n");

            sb.AppendLine("rankdir=LR");
            sb.AppendLine("node [" +
                "shape=rect " +
                "width=0 height=0 margin=0.04 " +
                "color=\"lightsteelblue1\" " +
                "style=filled " +
                "fontsize=11]");

            string productCriticalNodeColor = "color=\"#76C272\"";

            IEnumerable<string> GetNodeAttributes(SourceBuildNode node)
            {
                if (GetEdgesWithUpstream(node.Identity).Any(e => e.ProductCritical))
                {
                    yield return productCriticalNodeColor;
                }
            }

            IEnumerable<string> GetEdgeAttributes(IEnumerable<SourceBuildEdge> edges)
            {
                if (edges.Any(e => e.ProductCritical))
                {
                    yield return "color=\"#00E22D\"";
                    yield return "penwidth=3";
                }
                else
                {
                    if (edges.Any(e => e.SkippedReason == null))
                    {
                        yield return "penwidth=2";
                    }
                    if (edges.Select(e => e.SkippedReason?.ToGraphVizAttributes())
                        .FirstOrDefault(c => c != null) is string skipAttributes)
                    {
                        yield return skipAttributes;
                    }
                }

                // These edges were all checked on the same cycle, so they should share this value.
                if (!edges.All(e => e.FirstDiscoverer))
                {
                    yield return "style=dashed";
                }
            }

            void AppendAttributes(IEnumerable<string> attrs)
            {
                var attrsArray = attrs.ToArray();
                if (attrsArray.Any())
                {
                    sb.Append("[");
                    sb.Append(string.Join(" ", attrsArray));
                    sb.Append("]");
                }
            }

            void AppendNode(SourceBuildNode n)
            {
                sb.Append("\"");
                sb.Append(n.Identity);
                sb.Append("\"");
            }

            // If the graph doesn't have its own single root, make a fake one.
            if (GetRootNodes().Count() != 1)
            {
                sb.Append("root[shape=circle fillcolor=\"chartreuse\"]\nroot -> {");
                foreach (var n in Nodes.Where(n => !GetDownstreams(n.Identity).Any()))
                {
                    AppendNode(n);
                    sb.Append(";");
                }
                sb.AppendLine("}");
            }

            foreach (var n in Nodes)
            {
                AppendNode(n);
                AppendAttributes(GetNodeAttributes(n));

                foreach (var edges in n.UpstreamEdges.NullAsEmpty()
                    .GroupBy(e => e, SourceBuildEdge.InOutComparer))
                {
                    sb.AppendLine();

                    foreach (var message in edges
                        .Where(e => e.SkippedReason != null)
                        .Select(e => $"/* {e.SkippedReason.Reason} {e.SkippedReason.Details} */")
                        .Distinct())
                    {
                        sb.AppendLine(message);
                    }

                    // Don't use grouping (A -> { B C }) so that we can apply attributes to each
                    // individual link.
                    sb.Append("\"");
                    sb.Append(n.Identity);
                    sb.Append("\" -> ");
                    AppendNode(IdentityNodes[edges.Key.Upstream]);
                    AppendAttributes(GetEdgeAttributes(edges));
                }

                sb.AppendLine();
            }

            if (includeCoherencyRedirects)
            {
                // Show how nodes were resolved while creating synthetic coherency, if applicable.
                foreach (var overrideGroup in AllEdges
                    .Where(e => e.OveriddenUpstreamForCoherency != null)
                    .GroupBy(e => e.Upstream, SourceBuildIdentity.CaseInsensitiveComparer))
                {
                    string nodeName =
                        $"\"Used {overrideGroup.Key}\\n" +
                        string.Join(
                            "\\n",
                            GetEdgesWithUpstream(overrideGroup.Key)
                                .Where(e => e.OveriddenUpstreamForCoherency == null)
                                .Select(e => e.Source?.Version)
                                .Where(v => v != null)
                                .Distinct()
                                .OrderBy(v => v)) +
                        $"\"";

                    sb.Append(nodeName);
                    AppendAttributes(GetNodeAttributes(IdentityNodes[overrideGroup.Key]));
                    sb.AppendLine();

                    sb.Append(nodeName);
                    sb.Append(" -> { ");

                    foreach (var overridden in overrideGroup
                        .GroupBy(g => g.OveriddenUpstreamForCoherency, SourceBuildIdentity.CaseInsensitiveComparer))
                    {
                        sb.Append("\"Instead of ");
                        sb.Append(overridden.Key);
                        sb.Append("\\n");
                        sb.Append(string.Join(
                            "\\n",
                            overridden
                                .Select(e => e.Source?.Version)
                                .Where(v => v != null)
                                .Distinct()
                                .OrderBy(v => v)));
                        sb.Append("\" ");
                    }

                    sb.AppendLine("}");
                }
            }

            if (includeLegend)
            {
                sb.AppendLine("\"Legend\" -> \"ordinary\"");

                sb.Append("\"Legend\" -> \"Target node already existed in graph from earlier cycle\\n(Combines with other styles)\"");
                AppendAttributes(GetEdgeAttributes(new[] { new SourceBuildEdge { FirstDiscoverer = false } }));
                sb.AppendLine();

                sb.AppendLine($"\"ProductCritical\"[{productCriticalNodeColor}]");
                sb.Append("\"Legend\" -> \"ProductCritical\"");
                AppendAttributes(GetEdgeAttributes(new[] { new SourceBuildEdge { ProductCritical = true, FirstDiscoverer = true } }));
                sb.AppendLine();

                foreach (SkipDependencyExplorationReason reason in
                    Enum.GetValues(typeof(SkipDependencyExplorationReason)))
                {
                    var explanation = new SkipDependencyExplorationExplanation
                    {
                        Reason = reason
                    };

                    sb.Append("\"Legend\" -> \"");
                    sb.Append(reason);
                    sb.Append("\"");
                    AppendAttributes(explanation.ToGraphVizAttributes().Split(' '));
                    sb.AppendLine();
                }

                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        public IEnumerable<SourceBuildEdge> GetEdgesWithUpstream(SourceBuildIdentity node) =>
            EdgesWithUpstream.GetOrDefault(node).NullAsEmpty();

        public IEnumerable<SourceBuildNode> GetDownstreams(SourceBuildIdentity node) =>
            GetEdgesWithUpstream(node)
                .Select(e => IdentityNodes[e.Downstream])
                .Distinct();

        public IEnumerable<SourceBuildNode> GetUpstreams(SourceBuildIdentity node) =>
            IdentityNodes[node].UpstreamEdges.NullAsEmpty()
                .Select(e => IdentityNodes[e.Upstream])
                .Distinct();

        public IEnumerable<SourceBuildNode> GetAllDownstreams(SourceBuildIdentity node) =>
            GetTraverseListCore(node, GetDownstreams);

        public IEnumerable<SourceBuildNode> GetAllUpstreams(SourceBuildIdentity node) =>
            GetTraverseListCore(node, GetUpstreams);

        public IEnumerable<SourceBuildNode> GetRootNodes() =>
            Nodes.Where(n => !GetDownstreams(n.Identity).Any());

        private IEnumerable<SourceBuildNode> GetTraverseListCore(
            SourceBuildIdentity start,
            Func<SourceBuildIdentity, IEnumerable<SourceBuildNode>> links)
        {
            var visited = new HashSet<SourceBuildIdentity>();
            var next = new Queue<SourceBuildIdentity>();
            next.Enqueue(start);

            while (next.Any())
            {
                SourceBuildIdentity node = next.Dequeue();

                foreach (var linkedNode in links(node))
                {
                    if (visited.Add(linkedNode.Identity))
                    {
                        yield return linkedNode;
                        next.Enqueue(linkedNode.Identity);
                    }
                }
            }
        }
    }
}
